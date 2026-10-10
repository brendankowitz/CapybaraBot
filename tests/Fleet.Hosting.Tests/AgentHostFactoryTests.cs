using Fleet.Hosting.Agents;

namespace Fleet.Hosting.Tests;

public sealed class AgentHostFactoryTests
{
    [Fact]
    public void Factory_selects_configured_host_per_agent()
    {
        var systemd = new StubHost();
        var podman = new StubHost();
        var factory = new AgentHostFactory(
            new Dictionary<AgentHostKind, IAgentHost> { [AgentHostKind.Systemd] = systemd, [AgentHostKind.Podman] = podman },
            new Dictionary<string, AgentHostKind> { ["code"] = AgentHostKind.Podman },
            AgentHostKind.Systemd);

        Assert.Same(podman, factory.GetHost("code"));
        Assert.Same(systemd, factory.GetHost("ea"));
    }

    private sealed class StubHost : IAgentHost
    {
        public ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default) => ValueTask.FromResult(new AgentHostHealth(true, "ok", DateTimeOffset.UtcNow));

        public ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ExecResult(0, string.Empty, string.Empty));

        public ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default) => ValueTask.FromResult(new SnapshotInfo("id", "path", DateTimeOffset.UtcNow));
    }
}
