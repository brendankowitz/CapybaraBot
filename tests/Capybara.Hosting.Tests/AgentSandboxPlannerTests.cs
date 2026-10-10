using System.Text.Json;
using Capybara.Hosting.Sandboxing;
using Capybara.Testing;

namespace Capybara.Hosting.Tests;

public sealed class AgentSandboxPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentSandboxOptions Options(string settingsDir) => new("/usr/bin/srt", "/opt/copilot/copilot", settingsDir);

    [Fact]
    public async Task Plan_writes_srt_settings_from_egress_domains_and_write_allowlist()
    {
        using var temp = new TempDirectory();
        var baseDir = Path.Combine(temp.Path, "agents", "code", "copilot");
        var workspace = Path.Combine(temp.Path, "workspace");
        var agent = TestAgents.Create("code", a => a with { EgressDomains = ["github.com", "*.githubusercontent.com", "GitHub.com"] });

        var plan = await new AgentSandboxPlanner(Options(Path.Combine(temp.Path, "srt"))).PlanAsync(agent, baseDir, workspace, cancellationToken: Ct);

        Assert.Equal(Path.Combine(temp.Path, "srt", "code.srt.json"), plan.SettingsPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(plan.SettingsPath, Ct));
        var network = json.RootElement.GetProperty("network");
        Assert.Equal(["github.com", "*.githubusercontent.com"], network.GetProperty("allowedDomains").EnumerateArray().Select(e => e.GetString()));
        var allowWrite = json.RootElement.GetProperty("filesystem").GetProperty("allowWrite").EnumerateArray().Select(e => e.GetString());
        Assert.Equal([Path.GetFullPath(workspace), Path.GetFullPath(baseDir)], allowWrite);
    }

    [Fact]
    public async Task Plan_returns_argument_list_without_shell_strings()
    {
        using var temp = new TempDirectory();
        var agent = TestAgents.Create("research");

        var plan = await new AgentSandboxPlanner(Options(temp.Path)).PlanAsync(agent, Path.Combine(temp.Path, "home"), Path.Combine(temp.Path, "ws"), ["--acp", "--stdio"], Ct);

        Assert.Equal("/usr/bin/srt", plan.Launch.Path);
        Assert.Equal(["--settings", plan.SettingsPath, "--", "/opt/copilot/copilot", "--acp", "--stdio"], plan.Launch.Arguments);
    }

    [Fact]
    public async Task Agent_without_egress_domains_gets_no_network()
    {
        using var temp = new TempDirectory();
        var plan = await new AgentSandboxPlanner(Options(temp.Path)).PlanAsync(TestAgents.Create("offline"), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(plan.SettingsPath, Ct));
        Assert.Empty(json.RootElement.GetProperty("network").GetProperty("allowedDomains").EnumerateArray());
    }

    [Fact]
    public async Task Replanning_replaces_settings_and_leaves_no_temp_files()
    {
        using var temp = new TempDirectory();
        var planner = new AgentSandboxPlanner(Options(temp.Path));
        await planner.PlanAsync(TestAgents.Create("code", a => a with { EgressDomains = ["old.example"] }), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        var plan = await planner.PlanAsync(TestAgents.Create("code", a => a with { EgressDomains = ["new.example"] }), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        var text = await File.ReadAllTextAsync(plan.SettingsPath, Ct);
        Assert.Contains("new.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old.example", text, StringComparison.Ordinal);
        Assert.Equal([plan.SettingsPath], Directory.GetFiles(temp.Path, "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("https://github.com")]
    [InlineData("github.com/path")]
    [InlineData("two words")]
    [InlineData("")]
    public async Task Invalid_egress_domain_is_rejected(string domain)
    {
        using var temp = new TempDirectory();
        var agent = TestAgents.Create("code", a => a with { EgressDomains = [domain] });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new AgentSandboxPlanner(Options(temp.Path)).PlanAsync(agent, Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct));
    }
}
