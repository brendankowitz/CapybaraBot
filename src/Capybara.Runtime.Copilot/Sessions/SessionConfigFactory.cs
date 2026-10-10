using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Tools;
using Capybara.Runtime.Copilot.Usage;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Builds the complete SDK session configuration. The same code path runs for create and resume, so tools,
/// permission handler, hooks, system message and event handlers are always re-registered (FR-AGT-03).
/// </summary>
public sealed class SessionConfigFactory(
    SessionHandlers handlers,
    UsageCapture usage,
    ISessionToolProvider tools,
    CopilotRuntimeOptions options)
{
    public SessionConfig CreateConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new SessionConfig { SessionId = session.SessionId }, session);
    }

    public ResumeSessionConfig ResumeConfig(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Apply(new ResumeSessionConfig { ContinuePendingWork = false }, session);
    }

    /// <summary>The tools the session may see: removed (not merely denied) at max depth and in consults.</summary>
    public static IReadOnlyList<string> AvailableToolsFor(CapybaraSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Kind == SessionKind.Consult
            ? FleetTools.ForConsult(session.Agent.Tools, session.Depth)
            : FleetTools.ForDepth(session.Agent.Tools, session.Depth);
    }

    private T Apply<T>(T config, CapybaraSession session)
        where T : SessionConfigBase
    {
        ArgumentNullException.ThrowIfNull(session);
        var agent = session.Agent;
        var available = AvailableToolsFor(session);

        config.Model = agent.Model;
        config.AvailableTools = [.. available];
        config.Tools = [.. tools.GetTools(session, [.. available])];
        config.SystemMessage = SystemMessageBuilder.Build(agent, session.Kind);
        config.WorkingDirectory = options.AgentWorkingDirectory(agent.Id);

        config.OnPermissionRequest = (request, _) => handlers.OnPermissionRequestAsync(session, request);
        config.OnUserInputRequest = (request, _) => handlers.OnUserInputRequestAsync(session, request);
        config.Hooks = new SessionHooks
        {
            OnPreToolUse = async (input, _) => await handlers.OnPreToolUseAsync(session, input),
        };
        config.OnEvent = e => usage.OnEvent(session, e);

        config.SessionLimits = new SessionLimitsConfig
        {
            MaxAiCredits = (double)Math.Min(agent.Budget.SessionMaxCredits, session.Envelope.Budget.Credits),
        };
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = session.Kind == SessionKind.Main };

        // The SDK's cross-session store would bypass memory scoping; the wiki is the only memory (FR-MEM-01).
        config.EnableSessionStore = false;
        config.EnableSkills = true;
        config.SkillDirectories = [.. options.SharedSkillDirectories, options.AgentSkillDirectory(agent.Id)];
        return config;
    }
}
