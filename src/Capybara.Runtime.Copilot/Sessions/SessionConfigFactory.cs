using Capybara.Connections.Client;
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;
using RemoteSessionMode = GitHub.Copilot.Rpc.RemoteSessionMode;
using UIElicitationResponseAction = GitHub.Copilot.Rpc.UIElicitationResponseAction;

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
    ISecretStore? secrets = null,
    IBrokerToolSource? broker = null)
{
    /// <summary>
    /// Runtime tools the host enables beyond the agent's allowlist. They may or may not be present at any moment
    /// (tool search only appears once tools are deferred), so the self-test neither requires nor rejects them.
    /// </summary>
    /// <remarks>The name matches the SDK's internal <c>CopilotSession.ToolSearchToolName</c>, which is not public.</remarks>
    public static readonly IReadOnlyList<string> InfrastructureTools = ["tool_search_tool"];

    /// <summary>
    /// The smallest <c>SessionLimits.MaxAiCredits</c> the Copilot CLI accepts: <c>session.create</c> fails with
    /// "Minimum session limit is 30 AI credits" below it (verified against the bundled CLI).
    /// </summary>
    public const decimal SdkMinimumSessionCredits = 30m;

    /// <summary>Lifetime advertised for the session-scoped token; the SDK requires a positive value (eight hours is typical).</summary>
    private const long GitHubTokenLifetimeSeconds = 8 * 60 * 60;

    private readonly SkillResolver _skills = new(options.BuiltInSkillDirectory, options.ConfigRoot);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _limitsLogged = new(StringComparer.Ordinal);

    public async ValueTask<SessionConfig> CreateConfigAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var resolved = await ResolveSecretsAsync(session.Agent, cancellationToken);
        var brokerTools = await BrokerToolsAsync(session, cancellationToken);
        return Apply(new SessionConfig { SessionId = session.SessionId }, session, resolved, brokerTools);
    }

    public async ValueTask<ResumeSessionConfig> ResumeConfigAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var resolved = await ResolveSecretsAsync(session.Agent, cancellationToken);
        var brokerTools = await BrokerToolsAsync(session, cancellationToken);
        return Apply(new ResumeSessionConfig { ContinuePendingWork = false }, session, resolved, brokerTools);
    }

    /// <summary>Synchronous build for agents that need no secrets, on hosts without a broker.</summary>
    /// <exception cref="InvalidOperationException">The agent needs a secret, or broker tools exist; use <see cref="CreateConfigAsync"/>.</exception>
    public SessionConfig CreateConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new SessionConfig { SessionId = session.SessionId }, session, NoSecrets(session.Agent), NoBroker());
    }

    /// <summary>Synchronous build for agents that need no secrets, on hosts without a broker.</summary>
    /// <exception cref="InvalidOperationException">The agent needs a secret, or broker tools exist; use <see cref="ResumeConfigAsync"/>.</exception>
    public ResumeSessionConfig ResumeConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new ResumeSessionConfig { ContinuePendingWork = false }, session, NoSecrets(session.Agent), NoBroker());
    }

    /// <summary>
    /// The reviewer's session (FR-SAF-05): no tools of any kind, no MCP servers, skills or memory, every permission and
    /// tool call refused, and nothing ever asked of a human. Its spend is charged to <paramref name="reviewed"/>.
    /// </summary>
#pragma warning disable GHCP001 // GitHubTokenProvider, SessionLimits and PermissionDecision are evaluation-only; the reviewer needs all three.
    public SessionConfig CreateReviewerConfig(string sessionId, string model, AgentId reviewed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new SessionConfig
        {
            SessionId = sessionId,
            Model = model,
            AvailableTools = [],
            Tools = [],
            McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal),
            EnableSkills = false,
            SkillDirectories = [],
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            RemoteSession = RemoteSessionMode.Off,
            GitHubTokenProvider = GitHubTokenProvider(),
            WorkingDirectory = options.ReviewerDirectory,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Customize,
                Sections = new Dictionary<SystemMessageSection, SectionOverride>
                {
                    [SystemMessageSection.Identity] = new() { Action = SectionOverrideAction.Replace, Content = "You are a security reviewer with no tools. You only classify the call you are shown." },
                    [SystemMessageSection.Safety] = new() { Action = SectionOverrideAction.Preserve },
                },
            },
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("the reviewer has no tools")),
            OnUserInputRequest = (_, _) => Task.FromResult(new UserInputResponse { Answer = "No one is available. Answer from the rubric.", WasFreeform = true }),
            OnElicitationRequest = _ => Task.FromResult(new ElicitationResult { Action = UIElicitationResponseAction.Decline }),
            Hooks = new SessionHooks
            {
                OnPreToolUse = (_, _) => Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput { PermissionDecision = "deny", PermissionDecisionReason = "the reviewer has no tools" }),
            },
            OnEvent = e => usage.OnReviewerEvent(reviewed, model, sessionId, e),
            SessionLimits = new SessionLimitsConfig { MaxAiCredits = (double)SdkSessionLimit(options.ReviewerMaxCredits) },
        };
    }
#pragma warning restore GHCP001

    /// <summary>
    /// The tools the session may see, source-qualified as <c>AvailableTools</c> expects: agent-to-agent tools are removed
    /// (not merely denied) at max depth (FR-A2A-06); consults lose memory writes and <c>propose_change</c>; incognito
    /// sessions lose every memory tool (FR-MEM-12). The host adds the task protocol tools to the session of a delegated
    /// task, and <c>answer_task</c> wherever the agent can delegate.
    /// </summary>
    public static IReadOnlyList<string> AvailableToolsFor(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var tools = session.Kind switch
        {
            SessionKind.Consult => FleetTools.ForConsult(session.Agent.Tools, session.Depth),
            SessionKind.Incognito => FleetTools.ForIncognito(session.Agent.Tools, session.Depth),
            _ => FleetTools.ForDepth(session.Agent.Tools, session.Depth),
        };

        if (session.Kind == SessionKind.Consult)
        {
            return tools;
        }

        var protocol = new List<string>();
        if (session.Kind == SessionKind.Task && session.TaskId is not null)
        {
            protocol.AddRange(FleetTools.TaskProtocol);
        }

        if (tools.Contains(FleetTools.DelegateTask, StringComparer.Ordinal))
        {
            protocol.Add(FleetTools.AnswerTask);
        }

        return [.. tools, .. protocol.Where(t => !tools.Contains(t, StringComparer.Ordinal))];
    }

    /// <summary>
    /// The SDK-side session limit for a configured budget: the budget itself, raised to the CLI's minimum. It is a soft
    /// backstop only; the host's per-turn credit limit is what enforces budgets (FR-OBS-02).
    /// </summary>
    public static decimal SdkSessionLimit(decimal configured) => Math.Max(SdkMinimumSessionCredits, configured);

    /// <summary>The host-implemented tools of <paramref name="session"/>, with their descriptions (for the self-test).</summary>
    public IReadOnlyList<AIFunctionDeclaration> HostToolsFor(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return tools.GetTools(session, [.. AvailableToolsFor(session)]);
    }

    /// <summary>
    /// The custom tools for <paramref name="session"/> as it is now (its depth may have changed since it was built), for
    /// <c>SetToolsAsync</c> on a live session. Lists broker tools afresh.
    /// </summary>
    public async ValueTask<IReadOnlyList<AIFunctionDeclaration>> BuildToolsAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var brokerTools = await BrokerToolsAsync(session, cancellationToken);
        return ComposeTools(session, AvailableToolsFor(session), brokerTools);
    }

    /// <summary>Host tools win a name clash: a broker tool can never shadow a fleet tool. Broker tools are gated.</summary>
    private List<AIFunctionDeclaration> ComposeTools(CapybaraSession session, IReadOnlyList<string> available, IReadOnlyList<AIFunction> brokerTools)
    {
        var hostTools = tools.GetTools(session, [.. available]);
        var hostNames = hostTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var gated = brokerTools.Where(t => !hostNames.Contains(t.Name)).ToList();
        session.SetCustomTools(hostNames, gated.Select(t => t.Name));
        return [.. hostTools, .. gated.Select(t => handlers.GateInvocation(session, t))];
    }

#pragma warning disable GHCP001 // GitHubTokenProvider (FR-SAF-08) and SessionLimits (soft credit backstop) are evaluation-only.
    private T Apply<T>(T config, CapybaraSession session, SessionSecrets resolved, IReadOnlyList<AIFunction> brokerTools)
        where T : SessionConfigBase
    {
        var agent = session.Agent;
        var available = AvailableToolsFor(session);

        config.Model = session.Model;
        config.RemoteSession = RemoteSessionFor(session);
        config.Provider = ProviderFor(agent.Provider, resolved.ProviderKey);
        config.GitHubTokenProvider = GitHubTokenProvider();
        config.AvailableTools = [.. available, .. InfrastructureTools];

        config.Tools = [.. ComposeTools(session, available, brokerTools)];
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

        // Budgets are enforced by the host, per turn (the turn's credit limit aborts the run). The SDK limit is a soft
        // backstop for sessions that live for one piece of work; the CLI refuses any below its minimum. Never for the main
        // session: the SDK window is cumulative over a live session (verified live), so the long-lived main session would
        // exhaust it within hours, lose its tools and fail every turn until a restart.
        config.SessionLimits = session.Kind == SessionKind.Main
            ? null
            : new SessionLimitsConfig { MaxAiCredits = (double)SdkSessionLimit(agent.Budget.SessionMaxCredits) };
        LogEffectiveLimits(agent);
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = session.Kind == SessionKind.Main };

        // The SDK's cross-session store would bypass memory scoping; the wiki is the only memory (FR-MEM-01).
        config.EnableSessionStore = false;

        ApplySkills(config, agent);
        return config;
    }
#pragma warning restore GHCP001

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
#pragma warning disable GHCP001 // RemoteSessionMode is evaluation-only; it must be set explicitly on every build (FR-CLI-08).
    private static RemoteSessionMode RemoteSessionFor(CapybaraSession session) =>
        session.Kind == SessionKind.Main && session.Agent.EnableRemoteSessions ? RemoteSessionMode.Export : RemoteSessionMode.Off;
#pragma warning restore GHCP001

    /// <summary>
    /// Supplies the GitHub token to the session over RPC, so it never sits in the CLI's environment where child
    /// processes inherit it (FR-SAF-08). The runtime asks again when the advertised lifetime runs out; the current
    /// option value is read each time, so a rotated token is picked up. The token is also registered with the redactor.
    /// </summary>
#pragma warning disable GHCP001 // The session-scoped token provider is evaluation-only; it keeps the token out of child environments (FR-SAF-08).
    private Func<GitHubTokenProviderArgs, Task<GitHubTokenProviderResult>>? GitHubTokenProvider()
    {
        if (options.GitHubToken is not { Length: > 0 } token)
        {
            return null;
        }

        handlers.Redactor.AddKnownSecret(token);
        if (!options.SessionScopedGitHubToken)
        {
            return null;
        }

        return _ =>
        {
            if (options.GitHubToken is not { Length: > 0 } current)
            {
                return Task.FromResult(GitHubTokenProviderResult.Cancel());
            }

            // A rotated token is registered too, so it is redacted like the one the factory first saw.
            handlers.Redactor.AddKnownSecret(current);
            return Task.FromResult(GitHubTokenProviderResult.FromToken(new GitHubToken { AccessToken = current, ExpiresIn = GitHubTokenLifetimeSeconds }));
        };
    }
#pragma warning restore GHCP001

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
    /// Vendored MCP servers (FR-CON-03). Only individually enabled tools are exposed; stdio servers must match their pins
    /// (FR-SKL-04) and start with an empty environment (<see cref="McpStdioLaunch"/>); http servers get their bearer token
    /// from the secret store.
    /// </summary>
    private Dictionary<string, McpServerConfig> McpServersFor(AgentDefinition agent, IReadOnlyDictionary<string, string> tokens)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var server in agent.McpServers)
        {
            int? timeout = server.TimeoutSeconds is { } seconds ? seconds * 1000 : null;
            servers[server.Name] = server.Type switch
            {
                "stdio" => McpStdioLaunch.Build(agent.Id, server, options, timeout),
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

    private IReadOnlyList<AIFunction> NoBroker() => broker is null
        ? []
        : throw new InvalidOperationException("Broker tools can only be listed asynchronously; build the session config with CreateConfigAsync or ResumeConfigAsync.");

    /// <summary>
    /// Broker tools come only from <see cref="IBrokerToolSource"/>, freshly on every create and resume (FR-AGT-03,
    /// FR-CON-03): the broker's MCP endpoint and credentials are never attached to a session, where the sandboxed CLI
    /// could read them. If the broker is unreachable the session is built without its tools and flagged, so the runtime
    /// rebuilds it before the next turn rather than failing every turn in the meantime.
    /// </summary>
    private async ValueTask<IReadOnlyList<AIFunction>> BrokerToolsAsync(CapybaraSession session, CancellationToken cancellationToken)
    {
        session.BrokerToolsUnavailable = false;
        if (broker is null)
        {
            return [];
        }

        try
        {
            return await broker.GetToolsAsync(session.Agent.Id.Value, session.SessionId, AvailableToolsFor(session), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogBrokerUnavailable(logger, ex, session.Agent.Id.Value, session.SessionId);
            session.BrokerToolsUnavailable = true;
            return [];
        }
    }

    private static IEnumerable<string> McpTokenNames(AgentDefinition agent) =>
        agent.McpServers.Where(s => s.Type == "http").Select(s => s.TokenSecret).OfType<string>().Distinct(StringComparer.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skill {Skill} ({Tier} tier) disabled for {Agent}: {Reason}")]
    private static partial void LogSkillRejected(ILogger logger, string skill, SkillTier tier, string agent, string reason);

    /// <summary>Once per agent: which limit actually applies, since the SDK value can differ from the configured one.</summary>
    private void LogEffectiveLimits(AgentDefinition agent)
    {
        if (_limitsLogged.TryAdd(agent.Id.Value, 0))
        {
            var turnLimit = agent.Budget.SessionMaxCredits;
            var sdkLimit = SdkSessionLimit(turnLimit);
            LogLimits(logger, agent.Id.Value, turnLimit, sdkLimit);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Credit limits for {Agent}: at most {TurnLimit} credits per turn, enforced by the host (abort); SDK backstop {SdkLimit} for task, consult and incognito sessions, none for the main session")]
    private static partial void LogLimits(ILogger logger, string agent, decimal turnLimit, decimal sdkLimit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broker unreachable while building session {SessionId} of {Agent}; its broker tools are missing until the session is rebuilt")]
    private static partial void LogBrokerUnavailable(ILogger logger, Exception exception, string agent, string sessionId);

    private sealed record SessionSecrets(string? ProviderKey, IReadOnlyDictionary<string, string> McpTokens);
}
