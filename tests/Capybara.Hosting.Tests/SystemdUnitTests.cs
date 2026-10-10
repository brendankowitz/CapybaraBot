using Capybara.Testing;

namespace Capybara.Hosting.Tests;

/// <summary>Guards the shipped systemd units against drifting from FR-HST-02 / FR-HST-05.</summary>
public sealed class SystemdUnitTests
{
    private static readonly string[] AllUnits = ["capybarad.service", "capybara-broker.service", "capybara-agent@.service", "capybara-backup.service"];

    private static readonly string[] RequiredNamespaces = ["user", "mnt", "pid"];

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

    /// <summary>Units whose process tree launches srt, i.e. bubblewrap (FR-HST-03).</summary>
    [Theory]
    [InlineData("capybarad.service")]
    [InlineData("capybara-agent@.service")]
    public void Units_that_launch_srt_allow_the_namespaces_and_syscalls_bubblewrap_needs(string unit)
    {
        var directives = Directives(unit).ToList();

        var namespaces = Assert.Single(directives, l => l.StartsWith("RestrictNamespaces=", StringComparison.Ordinal));
        Assert.NotEqual("RestrictNamespaces=yes", namespaces);
        var allowed = namespaces["RestrictNamespaces=".Length..].Split(' ');
        Assert.All(RequiredNamespaces, ns => Assert.Contains(ns, allowed));
        Assert.DoesNotContain(directives, l => l.StartsWith("RestrictNamespaces=~", StringComparison.Ordinal));
        Assert.Contains(directives, l => l.StartsWith("SystemCallFilter=", StringComparison.Ordinal) && l.Split(' ').Contains("@mount"));
        Assert.Contains(directives, l => l.StartsWith("SystemCallFilter=", StringComparison.Ordinal) && l.Split(' ').Contains("pivot_root"));
        Assert.DoesNotContain(directives, l => l.StartsWith("ProcSubset=", StringComparison.Ordinal) || l.StartsWith("ProtectProc=", StringComparison.Ordinal) || l.StartsWith("ProtectKernelTunables=", StringComparison.Ordinal));
        Assert.Contains("NoNewPrivileges=yes", directives);
        Assert.Contains("ProtectSystem=strict", directives);
        Assert.Contains("CapabilityBoundingSet=", directives);
    }

    [Fact]
    public void Broker_unit_keeps_strict_namespace_and_proc_hardening()
    {
        var directives = Directives("capybara-broker.service").ToList();

        Assert.Contains("RestrictNamespaces=yes", directives);
        Assert.Contains("ProtectProc=invisible", directives);
    }

    [Fact]
    public void Backup_unit_runs_capybarad_backup()
    {
        var directives = Directives("capybara-backup.service").ToList();

        Assert.Contains("ExecStart=/opt/capybara/capybarad backup", directives);
        Assert.Contains(directives, l => l.StartsWith("ReadWritePaths=", StringComparison.Ordinal) && l.Split(' ').Contains("/var/backups/capybara"));
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
