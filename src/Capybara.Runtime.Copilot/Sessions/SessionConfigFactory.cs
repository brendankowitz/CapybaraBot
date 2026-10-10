using Capybara.Contracts.Agents;
using Capybara.Contracts.Storage;
using Capybara.Orchestration.Skills;
using Capybara.Policy.Managed;
using Capybara.Policy.Rules;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Tools;
using Capybara.Runtime.Copilot.Usage;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using RemoteSessionMode = GitHub.Copilot.Rpc.RemoteSessionMode;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Builds the complete SDK session configuration. The same code path runs for create and resume, so tools, MCP servers,
/// managed settings, provider credentials, skills, permission/user-input/elicitation handlers, hooks, system message
/// and event handlers are always re-registered: the SDK persists none of them across resume (FR-AGT-03).
/// </summary>
/// <remarks>
/// Secrets (the BYOK provider key and http MCP bearer tokens) are fetched from <see cref="ISecretStore"/> on every
/// build, never cached, and registered with the shared redactor so they cannot leak back through tool output
/// (FR-AGT-10, FR-SAF-08). Only the async builders can fetch them.
/// </remarks>
public sealed partial class SessionConfigFactory(
    SessionHandlers handlers,
    UsageCapture usage,
    ISessionToolProvider tools,
    CopilotRuntimeOptions options,
    RulesEngine rules,
    ILogger<SessionConfigFactory> logger,
    ISecretStore? secrets = null)
{
    /// <summary>
    /// Runtime tools the host enables beyond the agent's allowlist. They may or may not be present at any moment
    /// (tool search only appears once tools are deferred), so the self-test neither requires nor rejects them.
    /// </summary>
    /// <remarks>The name matches the SDK's internal <c>CopilotSession.ToolSearchToolName</c>, which is not public.</remarks>
    public static readonly IReadOnlyList<string> InfrastructureTools = ["tool_search_tool"];

    private readonly SkillResolver _skills = new(options.BuiltInSkillDirectory, options.ConfigRoot);

    public async ValueTask<SessionConfig> CreateConfigAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var resolved = await ResolveSecretsAsync(session.Agent, cancellationToken);
        return Apply(new SessionConfig { SessionId = session.SessionId }, session, resolved);
    }

    public async ValueTask<ResumeSessionConfig> ResumeConfigAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var resolved = await ResolveSecretsAsync(session.Agent, cancellationToken);
        return Apply(new ResumeSessionConfig { ContinuePendingWork = false }, session, resolved);
    }

    /// <summary>Synchronous build for agents that need no secrets.</summary>
    /// <exception cref="InvalidOperationException">The agent needs a secret; use <see cref="CreateConfigAsync"/>.</exception>
    public SessionConfig CreateConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new SessionConfig { SessionId = session.SessionId }, session, NoSecrets(session.Agent));
    }

    /// <summary>Synchronous build for agents that need no secrets.</summary>
    /// <exception cref="InvalidOperationException">The agent needs a secret; use <see cref="ResumeConfigAsync"/>.</exception>
    public ResumeSessionConfig ResumeConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new ResumeSessionConfig { ContinuePendingWork = false }, session, NoSecrets(session.Agent));
    }

    /// <summary>
    /// The tools the session may see, source-qualified as <c>AvailableTools</c> expects: removed (not merely denied) at
    /// max depth and in consults.
    /// </summary>
    public static IReadOnlyList<string> AvailableToolsFor(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Kind == SessionKind.Consult
            ? FleetTools.ForConsult(session.Agent.Tools, session.Depth)
            : FleetTools.ForDepth(session.Agent.Tools, session.Depth);
    }

    private T Apply<T>(T config, CapybaraSession session, SessionSecrets resolved)
        where T : SessionConfigBase
    {
        var agent = session.Agent;
        var available = AvailableToolsFor(session);

        config.Model = agent.Model;
        config.RemoteSession = RemoteSessionFor(session);
        config.Provider = ProviderFor(agent.Provider, resolved.ProviderKey);
        config.AvailableTools = [.. available, .. InfrastructureTools];
        config.Tools = [.. tools.GetTools(session, [.. available])];
        config.McpServers = McpServersFor(agent, resolved.McpTokens);
        config.ToolSearch = new ToolSearchConfig { Enabled = true, DeferThreshold = options.ToolSearchDeferThreshold };
        config.ManagedSettings = ManagedSettingsFor(agent);
        config.SystemMessage = SystemMessageBuilder.Build(agent, session.Kind);
        config.WorkingDirectory = options.AgentWorkingDirectory(agent.Id);

        config.OnPermissionRequest = (request, _) => handlers.OnPermissionRequestAsync(session, request);
        config.OnUserInputRequest = (request, _) => handlers.OnUserInputRequestAsync(session, request);
        config.OnElicitationRequest = context => handlers.OnElicitationRequestAsync(session, context);
        config.Hooks = new SessionHooks
        {
            OnPreToolUse = async (input, _) => await handlers.OnPreToolUseAsync(session, input),
            OnPostToolUse = (input, _) => handlers.OnPostToolUseAsync(session, input),
            OnPostToolUseFailure = (input, _) => handlers.OnPostToolUseFailureAsync(session, input),
        };
        config.OnEvent = e => usage.OnEvent(session, e);

        config.SessionLimits = new SessionLimitsConfig
        {
            MaxAiCredits = (double)Math.Min(agent.Budget.SessionMaxCredits, session.Envelope.Budget.Credits),
        };
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = session.Kind == SessionKind.Main };

        // The SDK's cross-session store would bypass memory scoping; the wiki is the only memory (FR-MEM-01).
        config.EnableSessionStore = false;

        ApplySkills(config, agent);
        return config;
    }

    /// <summary>
    /// Tiered, pinned and scanned skills only (FR-SKL-01, FR-SKL-04); bodies load on demand (FR-SKL-02).
    /// </summary>
    /// <remarks>
    /// Each verified skill folder is passed as its own root, never a tier root. The runtime walks a root for
    /// <c>SKILL.md</c> at any depth, so a tier root would also load what the resolver never verified: staged
    /// <c>pending/{name}</c> proposals, <c>_</c>-prefixed folders and nested layouts. A verified folder that itself
    /// contains a nested <c>SKILL.md</c> is excluded for the same reason. <c>DisabledSkills</c> is kept as defence in
    /// depth only; it cannot separate a pending proposal from the active skill of the same name.
    /// </remarks>
    private void ApplySkills(SessionConfigBase config, AgentDefinition agent)
    {
        var skills = _skills.Resolve(agent.Id);
        foreach (var rejected in skills.Rejected)
        {
            LogSkillRejected(logger, rejected.Name, rejected.Tier, agent.Id.Value, rejected.Reason);
        }

        var directories = new List<string>();
        var disabled = new SortedSet<string>(skills.Disabled, StringComparer.Ordinal);
        foreach (var skill in skills.Active)
        {
            if (HasNestedSkill(skill.Directory))
            {
                LogSkillRejected(logger, skill.Name, skill.Tier, agent.Id.Value, "contains a nested SKILL.md");
                disabled.Add(skill.Name);
                continue;
            }

            directories.Add(skill.Directory);
        }

        config.EnableSkills = true;
        config.SkillDirectories = directories;
        config.DisabledSkills = [.. disabled];
    }

    private static bool HasNestedSkill(string skillDirectory) =>
        Directory.EnumerateFiles(skillDirectory, "SKILL.md", SearchOption.AllDirectories)
            .Any(f => !string.Equals(Path.GetDirectoryName(f), Path.TrimEndingDirectorySeparator(skillDirectory), StringComparison.Ordinal));

    /// <summary>
    /// FR-CLI-08: an opted-in agent's main session is exported to GitHub (Mission Control) read-only. Never <c>On</c>:
    /// remote steering would let GitHub web/mobile drive the agent around the gateway's tailnet authentication and the
    /// inbox. Consult and task sessions are never exported. Always set explicitly, because the setting is not persisted
    /// across resume.
    /// </summary>
    private static RemoteSessionMode RemoteSessionFor(CapybaraSession session) =>
        session.Kind == SessionKind.Main && session.Agent.EnableRemoteSessions ? RemoteSessionMode.Export : RemoteSessionMode.Off;

    /// <summary>FR-SAF-01 layer 1: the runtime enforces static deny/ask itself, as defense in depth.</summary>
    private ManagedSettings ManagedSettingsFor(AgentDefinition agent)
    {
        var plan = ManagedSettingsPlanner.Plan(agent, rules.Rules);
        return new ManagedSettings
        {
            Permissions = new ManagedSettingsPermissions { Deny = [.. plan.Deny], Ask = [.. plan.Ask] },
        };
    }

    private static ProviderConfig? ProviderFor(ModelProviderSpec? spec, string? key) => spec is null
        ? null
        : new ProviderConfig
        {
            Type = spec.Type,
            BaseUrl = spec.BaseUrl,
            WireApi = spec.WireApi,
            ModelId = spec.ModelId,
            ApiKey = key,
            Azure = spec.AzureApiVersion is { } version ? new AzureOptions { ApiVersion = version } : null,
        };

    /// <summary>
    /// Vendored MCP servers (FR-CON-03). Only individually enabled tools are exposed; stdio executables must match
    /// their pin (FR-SKL-04); http servers get their bearer token from the secret store.
    /// </summary>
    private Dictionary<string, McpServerConfig> McpServersFor(AgentDefinition agent, IReadOnlyDictionary<string, string> tokens)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var server in agent.McpServers)
        {
            int? timeout = server.TimeoutSeconds is { } seconds ? seconds * 1000 : null;
            servers[server.Name] = server.Type switch
            {
                "stdio" => Stdio(agent.Id, server, timeout),
                "http" => new McpHttpServerConfig
                {
                    Url = server.Url ?? throw new InvalidOperationException($"MCP server '{server.Name}' of {agent.Id} has no URL."),
                    Tools = [.. server.Tools],
                    Timeout = timeout,
                    Headers = server.TokenSecret is { } secret
                        ? new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {tokens[secret]}" }
                        : null,
                },
                _ => throw new InvalidOperationException($"MCP server '{server.Name}' of {agent.Id} has unsupported type '{server.Type}'."),
            };
        }

        return servers;
    }

    private McpStdioServerConfig Stdio(AgentId agent, McpServerSpec server, int? timeout) => new()
    {
        Command = McpServerPins.Verify(agent, server),
        Args = [.. server.Args],
        Tools = [.. server.Tools],
        Timeout = timeout,
        Env = MinimalEnvironment(agent),
    };

    /// <summary>
    /// An explicit, minimal environment for stdio MCP servers, so the CLI's environment (which carries the GitHub token)
    /// and the host's are not handed to third-party code. <c>HOME</c> is the agent's workspace, the only place the
    /// sandbox lets it write.
    /// </summary>
    private Dictionary<string, string> MinimalEnvironment(AgentId agent) => new(StringComparer.Ordinal)
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path ? path : "/usr/local/bin:/usr/bin:/bin",
        ["HOME"] = options.AgentWorkingDirectory(agent),
        ["LANG"] = Environment.GetEnvironmentVariable("LANG") is { Length: > 0 } lang ? lang : "C.UTF-8",
    };

    private async ValueTask<SessionSecrets> ResolveSecretsAsync(AgentDefinition agent, CancellationToken cancellationToken)
    {
        string? providerKey = null;
        if (agent.Provider?.ApiKeySecret is { } keyName)
        {
            providerKey = await FetchAsync(agent, keyName, cancellationToken);
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tokenName in McpTokenNames(agent))
        {
            tokens[tokenName] = await FetchAsync(agent, tokenName, cancellationToken);
        }

        return new SessionSecrets(providerKey, tokens);
    }

    private async ValueTask<string> FetchAsync(AgentDefinition agent, string name, CancellationToken cancellationToken)
    {
        var store = secrets ?? throw new InvalidOperationException($"{agent.Id} needs secret '{name}' but no secret store is registered.");
        var value = await store.GetAsync(name, cancellationToken)
            ?? throw new InvalidOperationException($"Secret '{name}' for {agent.Id} is missing from the secret store.");
        handlers.Redactor.AddKnownSecret(value);
        return value;
    }

    private static SessionSecrets NoSecrets(AgentDefinition agent)
    {
        string[] needed = [.. agent.Provider?.ApiKeySecret is { } key ? [key] : Array.Empty<string>(), .. McpTokenNames(agent)];
        return needed.Length == 0
            ? new SessionSecrets(null, new Dictionary<string, string>())
            : throw new InvalidOperationException(
                $"{agent.Id} needs secrets ({string.Join(", ", needed)}); build its session config with CreateConfigAsync or ResumeConfigAsync.");
    }

    private static IEnumerable<string> McpTokenNames(AgentDefinition agent) =>
        agent.McpServers.Where(s => s.Type == "http").Select(s => s.TokenSecret).OfType<string>().Distinct(StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skill {Skill} ({Tier} tier) disabled for {Agent}: {Reason}")]
    private static partial void LogSkillRejected(ILogger logger, string skill, SkillTier tier, string agent, string reason);

    private sealed record SessionSecrets(string? ProviderKey, IReadOnlyDictionary<string, string> McpTokens);
}
