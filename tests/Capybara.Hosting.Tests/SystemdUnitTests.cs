using Capybara.Testing;

namespace Capybara.Hosting.Tests;

/// <summary>Guards the shipped systemd units against drifting from FR-HST-02 / FR-HST-05.</summary>
public sealed class SystemdUnitTests
{
    private static readonly string[] AllUnits = ["capybarad.service", "capybara-broker.service", "capybara-agent@.service", "capybara-backup.service"];

    private static readonly string[] ProducedExecutables = ["/opt/capybara/capybarad", "/opt/capybara/capybara-broker"];

    private static string[] Unit(string name) => File.ReadAllLines(Path.Combine(RepositoryPaths.Root, "deploy", "systemd", name));

    private static IEnumerable<string> Directives(string name) =>
        Unit(name).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    [Theory]
    [InlineData("capybarad.service")]
    [InlineData("capybara-broker.service")]
    [InlineData("capybara-agent@.service")]
    [InlineData("capybara-backup.service")]
    public void No_unit_sets_MemoryDenyWriteExecute(string unit) =>
        Assert.DoesNotContain(Directives(unit), l => l.StartsWith("MemoryDenyWriteExecute", StringComparison.Ordinal));

    [Fact]
    public void Capybarad_unit_is_notify_with_resource_and_network_limits()
    {
        var directives = Directives("capybarad.service").ToList();

        Assert.Contains("Type=notify", directives);
        Assert.Contains(directives, l => l.StartsWith("MemoryMax=", StringComparison.Ordinal));
        Assert.Contains(directives, l => l.StartsWith("CPUQuota=", StringComparison.Ordinal));
        Assert.Contains("IPAddressDeny=any", directives);
        Assert.Contains("IPAddressAllow=localhost", directives);
        Assert.Contains("LoadCredentialEncrypted=github-token:/etc/capybara/credentials/github-token.cred", directives);
    }

    [Fact]
    public void Backup_unit_runs_capybarad_backup()
    {
        var directives = Directives("capybara-backup.service").ToList();

        Assert.Contains("ExecStart=/opt/capybara/capybarad backup", directives);
    }

    [Fact]
    public void Agent_unit_runs_capybarad_in_agent_mode_and_is_resource_limited()
    {
        var directives = Directives("capybara-agent@.service").ToList();

        Assert.Contains("ExecStart=/opt/capybara/capybarad agent %i", directives);
        Assert.Contains("Type=notify", directives);
        Assert.Contains(directives, l => l.StartsWith("MemoryMax=", StringComparison.Ordinal));
        Assert.Contains(directives, l => l.StartsWith("CPUQuota=", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_unit_executable_is_capybarad_or_capybara_broker()
    {
        var executables = AllUnits
            .SelectMany(Directives)
            .Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))
            .Select(l => l["ExecStart=".Length..].Split(' ')[0]);

        Assert.All(executables, e => Assert.Contains(e, ProducedExecutables));
    }
}
