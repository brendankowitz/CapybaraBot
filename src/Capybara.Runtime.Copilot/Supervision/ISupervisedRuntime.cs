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

    /// <summary>
    /// The supervisor's restart: confirms the runtime is still down and, only then, asks <paramref name="confirmRestart"/>
    /// (which records the crash and returns <see langword="false"/> to leave it down, e.g. quarantined) and restarts it.
    /// Returns whether it was down. A runtime whose own lifecycle changes concurrently (<see cref="AgentRuntime"/>, during
    /// a definition swap) must do the check and the restart under one lock; the default suits runtimes without that.
    /// </summary>
    async Task<bool> RestartIfDownAsync(Func<bool> confirmRestart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmRestart);
        if (await PingAsync(cancellationToken))
        {
            return false;
        }

        if (confirmRestart())
        {
            await RestartAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>Compares the tools the runtime exposes with the agent's allowlist (FR-AGT-09).</summary>
    Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken);
}
