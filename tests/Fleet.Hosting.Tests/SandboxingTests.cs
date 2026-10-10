using Fleet.Hosting.Sandboxing;

namespace Fleet.Hosting.Tests;

public sealed class SandboxingTests
{
    [Fact]
    public void Srt_policy_json_contains_domain_and_write_allowlists()
    {
        var json = SrtPolicyBuilder.Build(new SrtPolicy("code", ["github.com"], ["/workspace"]));

        Assert.Contains("github.com", json, StringComparison.Ordinal);
        Assert.Contains("/workspace", json, StringComparison.Ordinal);
        Assert.Contains("denyByDefault", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_launcher_uses_argument_lists()
    {
        var launch = SandboxedCliLauncher.ForSrt("srt", "settings.json", "copilot", ["agent"]);

        Assert.Equal("srt", launch.Path);
        Assert.Equal(["run", "--settings", "settings.json", "--", "copilot", "agent"], launch.Arguments);
    }
}
