namespace Capybara.Hosting.Agents;

/// <summary>Abstraction over agent runtime hosts: systemd, Podman, ACA Sandboxes, or hybrid relays (FR-HST-01, FR-HST-09).</summary>
public interface IAgentHost
{
    ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default);

    ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default);

    ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default);

    ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default);

    ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default);

    ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default);
}
