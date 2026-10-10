using Capybara.Hosting.Backup;
using Capybara.Hosting.DependencyInjection;
using Capybara.Hosting.Storage;
using Capybara.Testing;
using Microsoft.Extensions.Configuration;

namespace Capybara.Hosting.Tests;

public sealed class BackupCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Env : IDisposable
    {
        private readonly TempDirectory _root = new();

        public Env()
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(Path.Combine(Home, "session.txt"), "state");
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capybara:Backup:Sources:0:Name"] = "agent-code",
                ["Capybara:Backup:Sources:0:Path"] = Home,
                ["Capybara:Backup:WorkingDirectory"] = Path.Combine(_root.Path, "work"),
            }).Build();
            Hosting = new HostingOptions { BlobRoot = Path.Combine(_root.Path, "blobs") };
        }

        public string Home => Path.Combine(_root.Path, "home");

        public string Restore => Path.Combine(_root.Path, "restore");

        public IConfiguration Configuration { get; }

        public HostingOptions Hosting { get; }

        public StringWriter Out { get; } = new();

        public StringWriter Err { get; } = new();

        public Task<int> RunAsync(params string[] args) => BackupCommand.RunAsync(args, Configuration, Hosting, Out, Err, cancellationToken: Ct);

        public void Dispose() => _root.Dispose();
    }

    [Theory]
    [InlineData("backup", true)]
    [InlineData("restore", true)]
    [InlineData("agent", false)]
    public void IsBackupCommand_recognises_only_backup_verbs(string verb, bool expected) =>
        Assert.Equal(expected, BackupCommand.IsBackupCommand([verb, "x"]));

    [Fact]
    public void IsBackupCommand_is_false_without_arguments() => Assert.False(BackupCommand.IsBackupCommand([]));

    [Fact]
    public async Task Backup_then_restore_roundtrips_through_the_command()
    {
        using var env = new Env();

        Assert.Equal(BackupCommand.ExitOk, await env.RunAsync("backup"));
        var archive = await new FileSystemBlobStore(env.Hosting.BlobRoot).ListAsync("backups", Ct).SingleAsync(Ct);
        Assert.Contains(archive, env.Out.ToString(), StringComparison.Ordinal);

        Assert.Equal(BackupCommand.ExitOk, await env.RunAsync("restore", archive, env.Restore));
        Assert.Equal("state", await File.ReadAllTextAsync(Path.Combine(env.Restore, "agent-code", "session.txt"), Ct));
    }

    [Fact]
    public async Task Restore_of_a_tampered_archive_fails_and_writes_nothing()
    {
        using var env = new Env();
        await env.RunAsync("backup");
        var blobs = new FileSystemBlobStore(env.Hosting.BlobRoot);
        var archive = await blobs.ListAsync("backups", Ct).SingleAsync(Ct);
        var path = Path.Combine(env.Hosting.BlobRoot, archive.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, Ct);

        var exit = await env.RunAsync("restore", archive, env.Restore);

        Assert.Equal(BackupCommand.ExitFailed, exit);
        Assert.NotEmpty(env.Err.ToString());
        Assert.False(Directory.Exists(env.Restore) && Directory.EnumerateFileSystemEntries(env.Restore).Any());
    }

    [Fact]
    public async Task Restore_into_a_non_empty_destination_is_refused()
    {
        using var env = new Env();
        await env.RunAsync("backup");
        var archive = await new FileSystemBlobStore(env.Hosting.BlobRoot).ListAsync("backups", Ct).SingleAsync(Ct);
        await File.WriteAllTextAsync(Path.Combine(env.Home, "keep.txt"), "x", Ct);

        Assert.Equal(BackupCommand.ExitUsage, await env.RunAsync("restore", archive, env.Home));
    }

    [Fact]
    public async Task Restore_of_a_missing_archive_fails()
    {
        using var env = new Env();

        Assert.Equal(BackupCommand.ExitFailed, await env.RunAsync("restore", "backups/missing.tar.gz", env.Restore));
    }

    [Fact]
    public async Task Backup_without_configured_sources_is_a_usage_error()
    {
        using var env = new Env();

        var exit = await BackupCommand.RunAsync(["backup"], new ConfigurationBuilder().Build(), env.Hosting, env.Out, env.Err, cancellationToken: Ct);

        Assert.Equal(BackupCommand.ExitUsage, exit);
        Assert.Contains("Capybara:Backup:Sources", env.Err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup", "extra")]
    [InlineData("restore")]
    [InlineData("restore", "only-archive")]
    public async Task Wrong_arguments_are_a_usage_error(params string[] args)
    {
        using var env = new Env();

        Assert.Equal(BackupCommand.ExitUsage, await env.RunAsync(args));
    }

    [Fact]
    public void Sample_backup_configuration_binds_and_covers_the_required_data()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "deploy", "capybarad.backup.sample.json")));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Flatten(json.RootElement, "")).Build();

        var (options, schedule) = BackupConfiguration.Bind(configuration)!.Value;

        Assert.Equal(new TimeOnly(2, 0), schedule.UtcTimeOfDay);
        Assert.Contains(options.Sources, s => s.Path.EndsWith("/copilot", StringComparison.Ordinal));
        Assert.Contains(options.Sources, s => s.Name == "memory-repo");
        Assert.Contains(options.Sources, s => s.Kind == BackupSourceKind.SqliteDatabase);
        Assert.Contains(options.Sources, s => s.Path.EndsWith("/credentials", StringComparison.Ordinal));
    }

    private static IEnumerable<KeyValuePair<string, string?>> Flatten(System.Text.Json.JsonElement element, string prefix) =>
        element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}")),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().SelectMany((e, i) => Flatten(e, $"{prefix}:{i}")),
            _ => [new(prefix, element.ToString())],
        };
}
