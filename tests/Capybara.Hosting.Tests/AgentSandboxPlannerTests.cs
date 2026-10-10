using System.Text.Json;
using Capybara.Hosting.DependencyInjection;
using Capybara.Hosting.Sandboxing;
using Capybara.Testing;

namespace Capybara.Hosting.Tests;

public sealed class AgentSandboxPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Every TempDirectory lives under this root, so it stands in for the (relocatable) state root.
    private static readonly string TestStateRoot = Path.Combine(Path.GetTempPath(), "capybara-tests");

    private static AgentSandboxOptions Options(string settingsDir) => new("/usr/bin/srt", "/opt/copilot/copilot", settingsDir, [TestStateRoot]);

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
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("*.")]
    public async Task Invalid_egress_domain_is_rejected(string domain)
    {
        using var temp = new TempDirectory();
        var agent = TestAgents.Create("code", a => a with { EgressDomains = [domain] });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new AgentSandboxPlanner(Options(temp.Path)).PlanAsync(agent, Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Wildcard_under_a_registrable_domain_is_allowed()
    {
        using var temp = new TempDirectory();
        var agent = TestAgents.Create("code", a => a with { EgressDomains = ["*.github.com"] });

        var plan = await new AgentSandboxPlanner(Options(temp.Path)).PlanAsync(agent, Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        Assert.Contains("*.github.com", await File.ReadAllTextAsync(plan.SettingsPath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_denies_reads_of_state_config_and_credentials_and_reopens_only_own_directories()
    {
        using var temp = new TempDirectory();
        var baseDir = Path.Combine(temp.Path, "agents", "code", "copilot");
        var workspace = Path.Combine(temp.Path, "agents", "code", "workspace");
        var options = Options(Path.Combine(temp.Path, "srt")) with { DenyRead = AgentSandboxOptions.DefaultDenyRead(temp.Path, "/etc/capybara", "/run/credentials/capybarad.service") };

        var plan = await new AgentSandboxPlanner(options).PlanAsync(TestAgents.Create("code"), baseDir, workspace, cancellationToken: Ct);

        var filesystem = JsonDocument.Parse(await File.ReadAllTextAsync(plan.SettingsPath, Ct)).RootElement.GetProperty("filesystem");
        string?[] Strings(string name) => [.. filesystem.GetProperty(name).EnumerateArray().Select(e => e.GetString())];
        Assert.Equal(
            [Path.GetFullPath(temp.Path), Path.GetFullPath("/etc/capybara"), Path.GetFullPath("/run/credentials"), Path.GetFullPath("/run/credentials/capybarad.service")],
            Strings("denyRead"));
        Assert.Equal([Path.GetFullPath(workspace), Path.GetFullPath(baseDir)], Strings("allowRead"));
    }

    [Fact]
    public void Default_deny_read_covers_state_root_config_and_credentials()
    {
        var defaults = AgentSandboxOptions.DefaultDenyRead("/srv/capybara");

        Assert.Contains("/srv/capybara", defaults);
        Assert.DoesNotContain("/var/lib/capybara", defaults);
        Assert.Contains("/etc/capybara", defaults);
        Assert.Contains("/run/credentials", defaults);
        Assert.Contains("/run/credentials/capybarad.service", AgentSandboxOptions.DefaultDenyRead("/srv/capybara", credentialsDirectory: "/run/credentials/capybarad.service"));
    }

    [Fact]
    public void FromHosting_follows_a_relocated_state_root()
    {
        var options = AgentSandboxOptions.FromHosting(new HostingOptions { StateRoot = "/srv/capybara" }, "/usr/bin/srt", "/opt/copilot/copilot", "/srv/capybara/srt");

        Assert.Contains("/srv/capybara", options.DenyRead);
        Assert.DoesNotContain("/var/lib/capybara", options.DenyRead);
        Assert.Equal("/srv/capybara/srt", options.SettingsDirectory);
    }

    [Fact]
    public async Task Agent_directories_outside_every_denied_path_are_rejected_fail_closed()
    {
        using var temp = new TempDirectory();
        var relocated = AgentSandboxOptions.FromHosting(new HostingOptions { StateRoot = Path.Combine(temp.Path, "old-root") }, "/usr/bin/srt", "/opt/copilot/copilot", Path.Combine(temp.Path, "srt"));
        var planner = new AgentSandboxPlanner(relocated);

        // The agent lives under a state root that DenyRead does not cover (the classic relocated-StateRoot mistake).
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await planner.PlanAsync(TestAgents.Create("code"), Path.Combine(temp.Path, "new-root", "agents", "code", "copilot"), Path.Combine(temp.Path, "new-root", "agents", "code", "workspace"), cancellationToken: Ct));
        Assert.Contains("read-denied", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "srt", "code.srt.json")));
    }

    [Theory]
    [InlineData("old-root-evil/copilot", "old-root/ws")]
    [InlineData("old-root/copilot", "elsewhere/ws")]
    public async Task Rejection_applies_to_each_agent_directory_and_does_not_prefix_match(string baseRelative, string workspaceRelative)
    {
        using var temp = new TempDirectory();
        var denied = Path.Combine(temp.Path, "old-root");
        var planner = new AgentSandboxPlanner(new AgentSandboxOptions("/usr/bin/srt", "/opt/copilot/copilot", Path.Combine(temp.Path, "srt"), [denied]));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await planner.PlanAsync(TestAgents.Create("code"), Path.Combine(temp.Path, baseRelative), Path.Combine(temp.Path, workspaceRelative), cancellationToken: Ct));
    }

    [Fact]
    public async Task Agent_directories_under_a_relocated_state_root_are_accepted()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "srv");
        var options = AgentSandboxOptions.FromHosting(new HostingOptions { StateRoot = root }, "/usr/bin/srt", "/opt/copilot/copilot", Path.Combine(root, "srt"));

        var plan = await new AgentSandboxPlanner(options).PlanAsync(TestAgents.Create("code"), Path.Combine(root, "agents", "code", "copilot"), Path.Combine(root, "agents", "code", "workspace"), cancellationToken: Ct);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(plan.SettingsPath, Ct));
        Assert.Contains(Path.GetFullPath(root), json.RootElement.GetProperty("filesystem").GetProperty("denyRead").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Settings_file_is_write_denied()
    {
        using var temp = new TempDirectory();

        var plan = await new AgentSandboxPlanner(Options(Path.Combine(temp.Path, "srt"))).PlanAsync(TestAgents.Create("code"), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        var denyWrite = JsonDocument.Parse(await File.ReadAllTextAsync(plan.SettingsPath, Ct)).RootElement.GetProperty("filesystem").GetProperty("denyWrite");
        Assert.Equal([plan.SettingsPath], denyWrite.EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("w")]
    [InlineData("w/nested")]
    [InlineData("h")]
    [InlineData("h/policies")]
    public async Task Settings_directory_inside_a_writable_path_is_rejected(string relativeSettingsDir)
    {
        using var temp = new TempDirectory();
        var planner = new AgentSandboxPlanner(Options(Path.Combine(temp.Path, relativeSettingsDir)));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await planner.PlanAsync(TestAgents.Create("code"), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct));
        Assert.False(File.Exists(Path.Combine(temp.Path, relativeSettingsDir, "code.srt.json")));
    }

    [Fact]
    public async Task Settings_directory_next_to_a_writable_path_with_a_shared_name_prefix_is_accepted()
    {
        using var temp = new TempDirectory();
        var planner = new AgentSandboxPlanner(Options(Path.Combine(temp.Path, "w-settings")));

        var plan = await planner.PlanAsync(TestAgents.Create("code"), Path.Combine(temp.Path, "h"), Path.Combine(temp.Path, "w"), cancellationToken: Ct);

        Assert.True(File.Exists(plan.SettingsPath));
    }
}
