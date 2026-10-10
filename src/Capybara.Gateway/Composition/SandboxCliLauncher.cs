using Capybara.Contracts.Agents;
using Capybara.Hosting.Sandboxing;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;

namespace Capybara.Gateway.Composition;

/// <summary>
/// Launches each agent's Copilot CLI under <c>srt</c> with that agent's own egress allowlist and filesystem policy
/// (FR-AGT-07, FR-SAF-07). The settings file is rewritten on every start, so a config change takes effect on restart.
/// Planning failures propagate and fail the start (an agent never runs unsandboxed).
/// </summary>
internal sealed class SandboxCliLauncher(AgentSandboxPlanner planner, CopilotRuntimeOptions runtime) : IAgentCliLauncher
{
    public async ValueTask<CliLaunch> PlanAsync(AgentDefinition agent, CancellationToken cancellationToken)
    {
        var plan = await planner.PlanAsync(
            agent,
            runtime.AgentBaseDirectory(agent.Id.Value),
            runtime.AgentWorkingDirectory(agent.Id.Value),
            copilotArgs: null,
            cancellationToken);
        return new CliLaunch(plan.Launch.Path, plan.Launch.Arguments);
    }
}

/// <summary><c>Capybara:Runtime:Sandbox</c>: where <c>srt</c> and the sandboxed Copilot CLI live (FR-HST-03).</summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Capybara:Runtime:Sandbox";

    /// <summary>Path to <c>srt</c>. Unset disables per-agent sandboxing (development only).</summary>
    public string? SrtPath { get; set; }

    /// <summary>The Copilot CLI inside the sandbox; must be readable there, so install it outside the state root.</summary>
    public string? CopilotCliPath { get; set; }

    /// <summary>Where <c>{agent}.srt.json</c> files are written; defaults to <c>{StateRoot}/srt</c>.</summary>
    public string? SettingsDirectory { get; set; }
}
