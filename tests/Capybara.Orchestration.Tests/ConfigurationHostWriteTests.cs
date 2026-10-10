using System.Runtime.Versioning;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Rules;

namespace Capybara.Orchestration.Tests;

/// <summary>
/// The config host's write path: validation copies stay private and never include credentials; permission failures are
/// results, not crashes; written files are group-readable for capybara-broker and nothing else (FR-HST-02, FR-CON-02).
/// </summary>
public sealed class ConfigurationHostWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Validation_copies_go_to_the_scratch_root_and_never_include_credentials()
    {
        using var fixture = new ConfigFixture();
        fixture.Write("credentials/github-token.cred", "encrypted");
        fixture.Write("agents/alpha/byok.cred", "encrypted");
        var scratchRoot = Path.Combine(fixture.StateRoot, "tmp", "config-validation");
        using var host = CreateHost(fixture, scratchRoot);
        var scratchCopies = new List<(string Root, bool Credentials, bool StrayCred)>();

        var result = await host.ApplyAsync(
            root =>
            {
                if (!string.Equals(root, host.Root, StringComparison.Ordinal))
                {
                    scratchCopies.Add((root, Directory.Exists(Path.Combine(root, "credentials")), File.Exists(Path.Combine(root, "agents", "alpha", "byok.cred"))));
                }

                File.AppendAllText(Path.Combine(root, "agents", "alpha", "SOUL.md"), "More.\n");
            },
            "edit soul",
            Ct);

        Assert.Empty(result.Errors);
        var (scratch, credentials, strayCred) = Assert.Single(scratchCopies);
        Assert.StartsWith(scratchRoot + Path.DirectorySeparatorChar, scratch, StringComparison.Ordinal);
        Assert.False(credentials);
        Assert.False(strayCred);
        Assert.False(Directory.Exists(scratch));
        Assert.True(File.Exists(fixture.PathOf("credentials/github-token.cred")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(scratchRoot));
        }
    }

    [Fact]
    public async Task A_permission_failure_is_returned_as_an_error_not_thrown()
    {
        using var fixture = new ConfigFixture();

        var result = await fixture.Host.ApplyAsync(_ => throw new UnauthorizedAccessException("denied"), "edit", Ct);

        Assert.Contains(result.Errors, e => e.Contains("denied", StringComparison.Ordinal));
        Assert.Empty(fixture.Versioning.Commits);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Files_and_directories_written_into_the_tree_are_group_readable_and_not_world_readable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are not supported on Windows.");
        using var fixture = new ConfigFixture();
        var lifecycle = new Lifecycle.AgentLifecycleService(fixture.Host, fixture.StateRoot, [], fixture.Audit, fixture.Time);

        var result = await lifecycle.DuplicateAsync(Contracts.Agents.AgentId.Parse("alpha"), Contracts.Agents.AgentId.Parse("gamma"), "Gamma", ConfigPrincipal.Human("owner"), Ct);

        Assert.Empty(result.Errors);
        const UnixFileMode directory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        Assert.Equal(directory, File.GetUnixFileMode(fixture.PathOf("agents/gamma")));
        Assert.Equal(file, File.GetUnixFileMode(fixture.PathOf("agents/gamma/agent.yaml")));
        Assert.Equal(file, File.GetUnixFileMode(fixture.PathOf("agents/gamma/SOUL.md")));
    }

    private static CapybaraConfigurationHost CreateHost(ConfigFixture fixture, string scratchRoot)
    {
        var initial = CapybaraConfigurationLoader.Load(fixture.ConfigRoot);
        return new CapybaraConfigurationHost(fixture.ConfigRoot, initial, new AgentRegistry(initial.Agents, fixture.KillSwitch), new RulesEngine(initial.Rules), fixture.Versioning, scratchRoot);
    }
}
