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
        };
        config.OnEvent = e => usage.OnEvent(session, e);

        config.SessionLimits = new SessionLimitsConfig
        {
            MaxAiCredits = (double)Math.Min(agent.Budget.SessionMaxCredits, session.Envelope.Budget.Credits),
        };
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = session.Kind == SessionKind.Main };

        // The SDK's cross-session store would bypass memory scoping; the wiki is the only memory (FR-MEM-01).
        config.EnableSessionStore = false;

        // Tiered, pinned and scanned skills only (FR-SKL-01, FR-SKL-04). Bodies load on demand (FR-SKL-02).
        var skills = _skills.Resolve(agent.Id);
        foreach (var rejected in skills.Rejected)
        {
            LogSkillRejected(logger, rejected.Name, rejected.Tier, agent.Id.Value, rejected.Reason);
        }

        config.EnableSkills = true;
        config.SkillDirectories = [.. skills.Directories];
        config.DisabledSkills = [.. skills.Disabled];
        return config;
    }

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
    private static Dictionary<string, McpServerConfig> McpServersFor(AgentDefinition agent, IReadOnlyDictionary<string, string> tokens)
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

    private static McpStdioServerConfig Stdio(AgentId agent, McpServerSpec server, int? timeout)
    {
        return new McpStdioServerConfig
        {
            Command = McpServerPins.Verify(agent, server),
            Args = [.. server.Args],
            Tools = [.. server.Tools],
            Timeout = timeout,
        };
    }

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
