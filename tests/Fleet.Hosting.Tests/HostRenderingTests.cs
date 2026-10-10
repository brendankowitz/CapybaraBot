using Fleet.Hosting.Agents;

namespace Fleet.Hosting.Tests;

public sealed class HostRenderingTests
{
    private static AgentHostSpec Spec => new("code", "/var/lib/fleet/agents/code", "/workspace", ["github.com"], 536870912, "50%", "ghcr.io/example/fleet:1.0", new Dictionary<string, string> { ["COPILOT_HOME"] = "/home/fleet/.copilot" });

    [Fact]
    public void Systemd_drop_in_contains_hardening_directives()
    {
        var text = SystemdHost.RenderDropIn(Spec);

        Assert.Contains("MemoryMax=536870912", text, StringComparison.Ordinal);
        Assert.Contains("CPUQuota=50%", text, StringComparison.Ordinal);
        Assert.Contains("IPAddressDeny=any", text, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=yes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryDenyWriteExecute", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Podman_quadlet_contains_sandbox_hardening()
    {
        var text = PodmanHost.RenderQuadlet(Spec);

        Assert.Contains("ReadOnly=true", text, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", text, StringComparison.Ordinal);
        Assert.Contains("UserNS=keep-id", text, StringComparison.Ordinal);
        Assert.Contains("--network=none", text, StringComparison.Ordinal);
        Assert.Contains("seccomp", text, StringComparison.Ordinal);
    }
}
