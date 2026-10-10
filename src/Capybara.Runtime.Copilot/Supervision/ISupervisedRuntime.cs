using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Hosting;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>A runtime the supervisor can health-check, restart and self-test.</summary>
public interface ISupervisedRuntime
{
    AgentId Agent { get; }

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Ends the runtime's process; <see cref="StartAsync"/> or <see cref="RestartAsync"/> brings it back.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task RestartAsync(CancellationToken cancellationToken);

    /// <summary>Compares the tools the runtime exposes with the agent's allowlist (FR-AGT-09).</summary>
    Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken);
}
