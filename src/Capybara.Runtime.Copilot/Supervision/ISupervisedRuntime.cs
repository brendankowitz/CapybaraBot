using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>A runtime the supervisor can health-check and restart.</summary>
public interface ISupervisedRuntime
{
    AgentId Agent { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task RestartAsync(CancellationToken cancellationToken);
}
