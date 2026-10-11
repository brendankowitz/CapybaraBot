using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// Plans the command that starts one agent's CLI process, typically the <c>srt</c> sandbox wrapping the Copilot CLI with
/// that agent's own egress and filesystem policy (FR-AGT-07, FR-SAF-07). Without a registered launcher every agent uses
/// <see cref="Configuration.CopilotRuntimeOptions.CliPath"/> and <see cref="Configuration.CopilotRuntimeOptions.CliArgs"/>.
/// </summary>
/// <remarks>
/// Called on every start and restart. A launcher that throws fails the start, so an agent never runs unsandboxed
/// because its sandbox could not be planned (fail closed).
/// </remarks>
public interface IAgentCliLauncher
{
    ValueTask<CliLaunch> PlanAsync(AgentDefinition agent, CancellationToken cancellationToken);
}

/// <summary>An executable and its arguments, never a shell string. The SDK appends its own CLI flags after them.</summary>
public sealed record CliLaunch(string Path, IReadOnlyList<string> Arguments);
