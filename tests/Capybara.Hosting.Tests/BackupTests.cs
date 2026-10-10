using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Contracts.Storage;
using Capybara.Hosting.Backup;
using Capybara.Hosting.DependencyInjection;
using Capybara.Hosting.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Hosting.Tests;

public sealed class BackupTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture : IDisposable
    {
        private readonly LocalTempDirectory _root = new();

        public Fixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Restore);
            Blobs = new FileSystemBlobStore(BlobRoot);
        }

        public string Source => Path.Combine(_root.Path, "source");

        public string Restore => Path.Combine(_root.Path, "restore");

        public string Work => Path.Combine(_root.Path, "work");

        public string BlobRoot => Path.Combine(_root.Path, "blobs");

        public FileSystemBlobStore Blobs { get; }

        public BackupService Service(IBlobStore? blobs = null, TimeProvider? time = null, BackupOptions? options = null, params BackupSource[] sources) =>
            new(blobs ?? Blobs, time ?? TimeProvider.System, options ?? new BackupOptions(sources, WorkingDirectory: Work));

        public BackupService Service(params BackupSource[] sources) => Service(null, null, null, sources);

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task Backup_restore_roundtrips_and_detects_tamper()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Source, "agent"));
        await File.WriteAllTextAsync(Path.Combine(f.Source, "agent", "state.txt"), "hello", Ct);
        var service = f.Service(new BackupSource("agent", Path.Combine(f.Source, "agent")));

        var result = await service.BackupAsync(Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(f.Restore, "agent", "state.txt"), Ct));

        await f.Blobs.WriteAsync("backups/tampered.tar.gz", CreateTamperedArchive(result.Manifest), Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.RestoreAsync("backups/tampered.tar.gz", f.Restore, Ct));
    }

    [Fact]
    public async Task Multiple_sources_including_a_single_file_roundtrip()
    {
        using var f = new Fixture();
        var home = Directory.CreateDirectory(Path.Combine(f.Source, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(home, "nested"));
        await File.WriteAllTextAsync(Path.Combine(home, "nested", "a.txt"), "A", Ct);
        var cred = Path.Combine(f.Source, "token.cred");
        await File.WriteAllTextAsync(cred, "encrypted", Ct);
        var memory = Directory.CreateDirectory(Path.Combine(f.Source, "memory")).FullName;
        await File.WriteAllTextAsync(Path.Combine(memory, "wiki.md"), "# wiki", Ct);
        var service = f.Service(new BackupSource("home", home), new BackupSource("cred", cred), new BackupSource("memory", memory));

        var result = await service.BackupAsync(Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        Assert.Equal(["cred/token.cred", "home/nested/a.txt", "memory/wiki.md"], result.Manifest.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(f.Restore, "home", "nested", "a.txt"), Ct));
        Assert.Equal("encrypted", await File.ReadAllTextAsync(Path.Combine(f.Restore, "cred", "token.cred"), Ct));
        Assert.Equal("# wiki", await File.ReadAllTextAsync(Path.Combine(f.Restore, "memory", "wiki.md"), Ct));
    }

    [Fact]
    public async Task Working_directory_and_excluded_blob_root_under_a_source_are_not_archived()
    {
        using var f = new Fixture();
        var all = Directory.CreateDirectory(Path.Combine(f.Source, "all")).FullName;
        Directory.CreateDirectory(Path.Combine(all, "data"));
        await File.WriteAllTextAsync(Path.Combine(all, "data", "keep.txt"), "keep", Ct);
        var work = Path.Combine(all, "work");
        var blobRoot = Path.Combine(all, "blobs");
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "leftover.txt"), "scratch", Ct);
        var blobs = new FileSystemBlobStore(blobRoot);
        await blobs.WriteAsync("backups/20200101T000000000Z.tar.gz", new MemoryStream([1, 2, 3]), Ct);
        var service = new BackupService(blobs, TimeProvider.System, new BackupOptions([new BackupSource("all", all)], WorkingDirectory: work, ExcludedDirectories: [blobRoot]));

        var result = await service.BackupAsync(Ct);

        Assert.Equal("all/data/keep.txt", Assert.Single(result.Manifest.Entries).Path);
    }

    [Fact]
    public async Task Files_removed_while_the_backup_runs_are_skipped_not_fatal()
    {
        using var f = new Fixture();
        var dir = Directory.CreateDirectory(Path.Combine(f.Source, "many")).FullName;
        var names = Enumerable.Range(0, 1500).Select(i => $"f{i:D5}.txt").ToList();
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, name), name, Ct);
        }

        var service = f.Service(new BackupSource("many", dir));
        var deleter = Task.Run(
            () =>
            {
                foreach (var name in Enumerable.Reverse(names))
                {
                    try
                    {
                        File.Delete(Path.Combine(dir, name));
                    }
                    catch (IOException)
                    {
                        // Best effort: racing the backup is the point.
                    }
                }
            },
            Ct);

        var result = await service.BackupAsync(Ct);
        await deleter;
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        foreach (var entry in result.Manifest.Entries)
        {
            var restored = Path.Combine(f.Restore, entry.Path);
            Assert.Equal(Path.GetFileName(entry.Path), await File.ReadAllTextAsync(restored, Ct));
        }
    }

    [Fact]
    public async Task Missing_configured_source_fails_the_backup()
    {
        using var f = new Fixture();
        var service = f.Service(new BackupSource("gone", Path.Combine(f.Source, "does-not-exist")));

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await service.BackupAsync(Ct));
    }

    [Fact]
    public async Task Work_directory_is_left_empty_after_backup_and_restore()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var service = f.Service(new BackupSource("s", f.Source));

        var result = await service.BackupAsync(Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
    }

    // ---- SQLite (FR-HST-05: consistent snapshots of WAL-mode stores) ----

    [Fact]
    public async Task Sqlite_source_is_snapshotted_consistently_while_a_writer_is_active()
    {
        using var f = new Fixture();
        var dbPath = Path.Combine(f.Source, "state.db");
        await using var writer = await CreateWalDatabaseAsync(dbPath, rows: 500);
        var service = f.Service(new BackupSource("state", dbPath, BackupSourceKind.SqliteDatabase));

        BackupResult result;
        using (var stop = new CancellationTokenSource())
        {
            var writing = Task.Run(() => KeepWriting(writer, stop.Token), CancellationToken.None);
            result = await service.BackupAsync(Ct);
            await stop.CancelAsync();
            await writing;
        }

        Directory.CreateDirectory(Path.Combine(f.Restore, "state"));
        await File.WriteAllTextAsync(Path.Combine(f.Restore, "state", "state.db-wal"), "stale garbage", Ct);
        await File.WriteAllTextAsync(Path.Combine(f.Restore, "state", "state.db-shm"), "stale garbage", Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        var entry = Assert.Single(result.Manifest.Entries);
        Assert.Equal("state/state.db", entry.Path);
        Assert.True(entry.IsSqlite);
        Assert.False(File.Exists(Path.Combine(f.Restore, "state", "state.db-wal")));
        Assert.False(File.Exists(Path.Combine(f.Restore, "state", "state.db-shm")));
        await AssertHealthyDatabaseAsync(Path.Combine(f.Restore, "state", "state.db"), minimumRows: 500);
    }

    [Fact]
    public async Task Sqlite_databases_inside_directory_sources_are_detected_and_their_sidecars_skipped()
    {
        using var f = new Fixture();
        var stores = Directory.CreateDirectory(Path.Combine(f.Source, "stores")).FullName;
        var dbPath = Path.Combine(stores, "scheduler.sqlite");
        await using var writer = await CreateWalDatabaseAsync(dbPath, rows: 300);
        await File.WriteAllTextAsync(Path.Combine(stores, "notes.db"), "not actually sqlite", Ct);
        await File.WriteAllTextAsync(Path.Combine(stores, "plain.txt"), "plain", Ct);
        var service = f.Service(new BackupSource("stores", stores));

        var result = await service.BackupAsync(Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        var byPath = result.Manifest.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        Assert.Equal(["stores/notes.db", "stores/plain.txt", "stores/scheduler.sqlite"], byPath.Keys.Order(StringComparer.Ordinal));
        Assert.True(byPath["stores/scheduler.sqlite"].IsSqlite);
        Assert.False(byPath["stores/notes.db"].IsSqlite);
        Assert.Equal("not actually sqlite", await File.ReadAllTextAsync(Path.Combine(f.Restore, "stores", "notes.db"), Ct));
        await AssertHealthyDatabaseAsync(Path.Combine(f.Restore, "stores", "scheduler.sqlite"), minimumRows: 300);
    }

    // ---- Restore hardening ----

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("agent/../../evil.txt")]
    [InlineData("agent/./a.txt")]
    [InlineData("agent\\..\\evil.txt")]
    public async Task Restore_rejects_unsafe_entry_names(string entryName)
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/bad.tar.gz", tar =>
        {
            AddFile(tar, entryName, "evil");
            AddManifest(tar, ManifestEntry(entryName, "evil"));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/bad.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_symlink_entries()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/link.tar.gz", tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "agent/link") { LinkName = "../../outside" });
            AddManifest(tar);
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/link.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_directory_entries()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/dir.tar.gz", tar =>
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "agent/"));
            AddManifest(tar);
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/dir.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_duplicate_archive_entries()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/dup.tar.gz", tar =>
        {
            AddFile(tar, "agent/a.txt", "one");
            AddFile(tar, "agent/a.txt", "two");
            AddManifest(tar, ManifestEntry("agent/a.txt", "two"));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/dup.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_archive_without_manifest()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/nomanifest.tar.gz", tar => AddFile(tar, "agent/a.txt", "a"));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/nomanifest.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_file_that_is_not_in_the_manifest()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/extra.tar.gz", tar =>
        {
            AddFile(tar, "agent/a.txt", "a");
            AddFile(tar, "agent/extra.txt", "sneaky");
            AddManifest(tar, ManifestEntry("agent/a.txt", "a"));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/extra.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_manifest_with_duplicate_paths_that_hides_an_extra_file()
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/dupmanifest.tar.gz", tar =>
        {
            AddFile(tar, "agent/a.txt", "a");
            AddFile(tar, "agent/b.txt", "b");
            AddManifest(tar, ManifestEntry("agent/a.txt", "a"), ManifestEntry("agent/a.txt", "a"));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/dupmanifest.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Theory]
    [InlineData("{\"createdAt\":\"2026-01-01T00:00:00Z\",\"entries\":null}")]
    [InlineData("{\"createdAt\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"createdAt\":\"2026-01-01T00:00:00Z\",\"entries\":[null]}")]
    [InlineData("this is { not json")]
    [InlineData("null")]
    public async Task Restore_rejects_malformed_manifests_with_InvalidDataException(string manifestJson)
    {
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/badmanifest.tar.gz", tar =>
        {
            AddFile(tar, "agent/a.txt", "a");
            AddRaw(tar, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await f.Service().RestoreAsync("backups/badmanifest.tar.gz", f.Restore, Ct));

        AssertNothingRestored(f);
    }

    [Fact]
    public async Task Restore_rejects_missing_archive_and_corrupt_gzip()
    {
        using var f = new Fixture();
        await f.Blobs.WriteAsync("backups/garbage.tar.gz", new MemoryStream(Encoding.UTF8.GetBytes("definitely not gzip")), Ct);
        var service = f.Service();

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await service.RestoreAsync("backups/none.tar.gz", f.Restore, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.RestoreAsync("backups/garbage.tar.gz", f.Restore, Ct));
    }

    // ---- Unix permissions ----

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Unix_modes_are_recorded_and_reapplied_on_restore()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are not supported on Windows.");
        using var f = new Fixture();
        var home = Directory.CreateDirectory(Path.Combine(f.Source, "home")).FullName;
        var secret = Path.Combine(home, "secret.cred");
        var script = Path.Combine(home, "run.sh");
        await File.WriteAllTextAsync(secret, "s", Ct);
        await File.WriteAllTextAsync(script, "#!/bin/sh", Ct);
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var service = f.Service(new BackupSource("home", home));

        var result = await service.BackupAsync(Ct);
        await service.RestoreAsync(result.Path, f.Restore, Ct);

        Assert.Equal(Convert.ToInt32("600", 8), result.Manifest.Entries.Single(e => e.Path == "home/secret.cred").UnixMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Restore, "home", "secret.cred")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(Path.Combine(f.Restore, "home", "run.sh")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Staging_directories_and_files_are_private_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are not supported on Windows.");
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var spy = new SpyBlobStore(f.Blobs);
        var service = f.Service(spy, null, null, new BackupSource("s", f.Source));

        await service.BackupAsync(Ct);

        Assert.StartsWith(Path.GetFullPath(f.Work), spy.StagedArchivePath, StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, spy.StagedArchiveMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, spy.StagedDirectoryMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(f.Work));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Restore_never_applies_setuid_bits_from_the_manifest()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are not supported on Windows.");
        using var f = new Fixture();
        await WriteArchiveAsync(f.Blobs, "backups/suid.tar.gz", tar =>
        {
            AddFile(tar, "agent/a.sh", "x");
            var entry = ManifestEntry("agent/a.sh", "x") with { UnixMode = Convert.ToInt32("4755", 8) };
            AddManifest(tar, entry);
        });

        await f.Service().RestoreAsync("backups/suid.tar.gz", f.Restore, Ct);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(Path.Combine(f.Restore, "agent", "a.sh")));
    }

    [Fact]
    public async Task Symlinked_files_in_sources_are_skipped()
    {
        using var f = new Fixture();
        var dir = Directory.CreateDirectory(Path.Combine(f.Source, "d")).FullName;
        var outside = Path.Combine(f.Source, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside", Ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "real.txt"), "real", Ct);
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symlinks is not permitted here.");
        }

        var result = await f.Service(new BackupSource("d", dir)).BackupAsync(Ct);

        Assert.Equal("d/real.txt", Assert.Single(result.Manifest.Entries).Path);
    }

    // ---- Retention ----

    [Fact]
    public async Task Backup_prunes_to_the_newest_RetainCount_archives_and_leaves_other_blobs()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        await f.Blobs.WriteAsync("backups/notes.txt", new MemoryStream([1]), Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero));
        var service = f.Service(null, time, new BackupOptions([new BackupSource("s", f.Source)], WorkingDirectory: f.Work, RetainCount: 3));

        BackupResult? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await service.BackupAsync(Ct);
            time.Advance(TimeSpan.FromDays(1));
        }

        var blobs = await ListAsync(f.Blobs, "backups");
        Assert.Equal(4, blobs.Count);
        Assert.Contains("backups/notes.txt", blobs);
        Assert.Contains(last!.Path, blobs);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 2, 0, 0, TimeSpan.Zero), await service.GetLastBackupTimeAsync(Ct));
        Assert.DoesNotContain("backups/20260301T020000000Z.tar.gz", blobs);
        Assert.DoesNotContain("backups/20260302T020000000Z.tar.gz", blobs);
    }

    [Fact]
    public void RetainCount_must_be_at_least_one()
    {
        using var f = new Fixture();

        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupService(f.Blobs, TimeProvider.System, new BackupOptions([], RetainCount: 0)));
    }

    // ---- Schedule ----

    [Fact]
    public void Schedule_computes_next_slot_and_last_slot_in_utc()
    {
        var schedule = new BackupScheduleOptions(new TimeOnly(2, 0));
        var before = new DateTimeOffset(2026, 1, 1, 1, 59, 0, TimeSpan.Zero);
        var exactly = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 1, 1, 2, 1, 0, TimeSpan.Zero);

        Assert.Equal(exactly, schedule.NextRunAfter(before));
        Assert.Equal(exactly.AddDays(-1), schedule.LastRunAtOrBefore(before));
        Assert.Equal(exactly.AddDays(1), schedule.NextRunAfter(exactly));
        Assert.Equal(exactly, schedule.LastRunAtOrBefore(exactly));
        Assert.Equal(exactly.AddDays(1), schedule.NextRunAfter(after));
        Assert.Equal(exactly, schedule.LastRunAtOrBefore(after));
    }

    [Fact]
    public async Task Hosted_service_runs_a_catch_up_backup_on_start_then_nightly_at_the_scheduled_time()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger<BackupHostedService>();
        var hosted = new BackupHostedService(
            f.Service(null, time, new BackupOptions([new BackupSource("s", f.Source)], WorkingDirectory: f.Work), []),
            new BackupScheduleOptions(new TimeOnly(2, 0)),
            time,
            logger);

        await hosted.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => logger.Count("Backup written") == 1);
            Assert.Single(await ListAsync(f.Blobs, "backups"));

            await Task.Delay(100, Ct); // let the worker register its next timer before time moves
            time.Advance(TimeSpan.FromHours(15)); // 01:00 next day: before the slot
            await Task.Delay(100, Ct);
            Assert.Equal(1, logger.Count("Backup written"));

            time.Advance(TimeSpan.FromHours(1.5)); // 02:30: slot passed
            await WaitUntilAsync(() => logger.Count("Backup written") == 2);
            Assert.Equal(2, (await ListAsync(f.Blobs, "backups")).Count);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_service_skips_catch_up_when_the_last_backup_is_current()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero));
        var service = f.Service(null, time, new BackupOptions([new BackupSource("s", f.Source)], WorkingDirectory: f.Work), []);
        await service.BackupAsync(Ct); // 03:00, after today's 02:00 slot
        time.Advance(TimeSpan.FromHours(1));
        var logger = new CapturingLogger<BackupHostedService>();
        var hosted = new BackupHostedService(service, new BackupScheduleOptions(new TimeOnly(2, 0)), time, logger);

        await hosted.StartAsync(Ct);
        try
        {
            await Task.Delay(300, Ct);
            Assert.Equal(0, logger.Count("Backup written"));
            Assert.Single(await ListAsync(f.Blobs, "backups"));

            time.Advance(TimeSpan.FromHours(23)); // 03:00 next day
            await WaitUntilAsync(() => logger.Count("Backup written") == 1);
            Assert.Equal(2, (await ListAsync(f.Blobs, "backups")).Count);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_service_survives_a_failing_backup_and_stays_alive_for_the_next_slot()
    {
        using var f = new Fixture();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var logger = new CapturingLogger<BackupHostedService>();
        var missing = Path.Combine(f.Source, "missing");
        var hosted = new BackupHostedService(
            f.Service(null, time, new BackupOptions([new BackupSource("s", missing)], WorkingDirectory: f.Work), []),
            new BackupScheduleOptions(new TimeOnly(2, 0)),
            time,
            logger);

        await hosted.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => logger.Count("Scheduled backup failed") == 1);

            Directory.CreateDirectory(missing);
            await File.WriteAllTextAsync(Path.Combine(missing, "a.txt"), "a", Ct);
            await Task.Delay(100, Ct);
            time.Advance(TimeSpan.FromHours(17));
            await WaitUntilAsync(() => logger.Count("Backup written") == 1);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_service_stops_cleanly_when_shutdown_cancels_a_running_backup()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var uploading = new TaskCompletionSource();
        var blobs = new HookedBlobStore(f.Blobs)
        {
            BeforeWrite = async cancellationToken =>
            {
                uploading.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            },
        };
        var logger = new CapturingLogger<BackupHostedService>();
        var hosted = new BackupHostedService(
            f.Service(blobs, time, new BackupOptions([new BackupSource("s", f.Source)], WorkingDirectory: f.Work)),
            BackupScheduleOptions.Default,
            time,
            logger);

        await hosted.StartAsync(Ct);
        await uploading.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await hosted.StopAsync(CancellationToken.None);

        Assert.True(hosted.ExecuteTask!.IsCompletedSuccessfully, $"Worker ended as {hosted.ExecuteTask.Status}.");
        Assert.Equal(0, logger.Count("Scheduled backup failed"));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
    }

    [Fact]
    public async Task Hosted_service_logs_a_stray_cancellation_and_keeps_running()
    {
        using var f = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "a.txt"), "a", Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var failuresLeft = 1;
        var blobs = new HookedBlobStore(f.Blobs)
        {
            BeforeWrite = _ => Interlocked.Decrement(ref failuresLeft) >= 0 ? throw new OperationCanceledException("not a shutdown") : Task.CompletedTask,
        };
        var logger = new CapturingLogger<BackupHostedService>();
        var hosted = new BackupHostedService(
            f.Service(blobs, time, new BackupOptions([new BackupSource("s", f.Source)], WorkingDirectory: f.Work)),
            new BackupScheduleOptions(new TimeOnly(2, 0)),
            time,
            logger);

        await hosted.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => logger.Count("Scheduled backup failed") == 1);
            await Task.Delay(100, Ct);
            time.Advance(TimeSpan.FromHours(17));
            await WaitUntilAsync(() => logger.Count("Backup written") == 1);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }
    // ---- Configuration / DI ----

    [Fact]
    public void Configuration_binds_sources_schedule_and_retention()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Capybara:Backup:Prefix"] = "nightly",
            ["Capybara:Backup:RetainCount"] = "7",
            ["Capybara:Backup:ScheduleUtc"] = "03:30",
            ["Capybara:Backup:WorkingDirectory"] = "/var/lib/capybara/work",
            ["Capybara:Backup:ExcludedDirectories:0"] = "/var/lib/capybara/blobs",
            ["Capybara:Backup:Sources:0:Name"] = "code-home",
            ["Capybara:Backup:Sources:0:Path"] = "/var/lib/capybara/agents/code/.copilot",
            ["Capybara:Backup:Sources:1:Name"] = "state",
            ["Capybara:Backup:Sources:1:Path"] = "/var/lib/capybara/state.db",
            ["Capybara:Backup:Sources:1:Kind"] = "sqlitedatabase",
        });

        var (options, schedule) = BackupConfiguration.Bind(configuration)!.Value;

        Assert.Equal("nightly", options.Prefix);
        Assert.Equal(7, options.RetainCount);
        Assert.Equal("/var/lib/capybara/work".Replace('/', Path.DirectorySeparatorChar), options.WorkingDirectory?.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(["/var/lib/capybara/blobs"], options.ExcludedDirectories);
        Assert.Equal(
            [new BackupSource("code-home", "/var/lib/capybara/agents/code/.copilot"), new BackupSource("state", "/var/lib/capybara/state.db", BackupSourceKind.SqliteDatabase)],
            options.Sources);
        Assert.Equal(new TimeOnly(3, 30), schedule.UtcTimeOfDay);
    }

    [Fact]
    public void Configuration_without_sources_disables_backup_and_bad_values_are_rejected()
    {
        Assert.Null(BackupConfiguration.Bind(Configuration([])));
        Assert.Throws<InvalidOperationException>(() => BackupConfiguration.Bind(Configuration(new Dictionary<string, string?>
        {
            ["Capybara:Backup:Sources:0:Name"] = "x",
        })));
        Assert.Throws<InvalidOperationException>(() => BackupConfiguration.Bind(Configuration(new Dictionary<string, string?>
        {
            ["Capybara:Backup:Sources:0:Name"] = "x",
            ["Capybara:Backup:Sources:0:Path"] = "/p",
            ["Capybara:Backup:ScheduleUtc"] = "25:99",
        })));
        Assert.Throws<InvalidOperationException>(() => BackupConfiguration.Bind(Configuration(new Dictionary<string, string?>
        {
            ["Capybara:Backup:Sources:0:Name"] = "x",
            ["Capybara:Backup:Sources:0:Path"] = "/p",
            ["Capybara:Backup:RetainCount"] = "0",
        })));
    }

    [Fact]
    public void AddCapybaraHosting_registers_backup_from_configuration_and_excludes_the_blob_root()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Capybara:Backup:ScheduleUtc"] = "04:15",
            ["Capybara:Backup:Sources:0:Name"] = "memory",
            ["Capybara:Backup:Sources:0:Path"] = "/var/lib/capybara/memory",
        });
        var services = new ServiceCollection();

        Capybara.Hosting.DependencyInjection.ServiceCollectionExtensions.AddCapybaraHosting(
            services,
            new Capybara.Hosting.DependencyInjection.HostingOptions { BlobRoot = "/var/lib/capybara/blobs" },
            configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<BackupOptions>();
        var schedule = provider.GetRequiredService<BackupScheduleOptions>();
        Assert.Equal("memory", Assert.Single(options.Sources).Name);
        Assert.Equal(["/var/lib/capybara/blobs"], options.ExcludedDirectories);
        Assert.Equal(new TimeOnly(4, 15), schedule.UtcTimeOfDay);
    }

    [Fact]
    public void AddCapybaraHosting_without_backup_settings_registers_no_backup_services()
    {
        var services = new ServiceCollection();

        services.AddCapybaraHosting(new HostingOptions(), Configuration([]));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(BackupService));
    }

    // ---- Helpers ----

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void AssertNothingRestored(Fixture f)
    {
        Assert.Empty(Directory.GetFileSystemEntries(f.Restore, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(f.Restore)!.FullName, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(Directory.GetParent(f.Restore)!.FullName)!.FullName, "evil.txt")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<List<string>> ListAsync(FileSystemBlobStore store, string prefix)
    {
        var result = new List<string>();
        await foreach (var path in store.ListAsync(prefix, Ct))
        {
            result.Add(path);
        }

        return result;
    }

    private static async Task<SqliteConnection> CreateWalDatabaseAsync(string path, int rows)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
        await command.ExecuteNonQueryAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        for (var i = 0; i < rows; i++)
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO t(v) VALUES ('row');";
            await command.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        Assert.True(File.Exists(path + "-wal"), "Test setup: the live database should have a -wal file.");
        return connection;
    }

    private static void KeepWriting(SqliteConnection connection, CancellationToken stop)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t(v) VALUES (hex(randomblob(512)));";
        while (!stop.IsCancellationRequested)
        {
            command.ExecuteNonQuery();
            Thread.Sleep(1);
        }
    }

    private static async Task AssertHealthyDatabaseAsync(string path, int minimumRows)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(Ct);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await integrity.ExecuteScalarAsync(Ct));
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t;";
        Assert.True((long)(await count.ExecuteScalarAsync(Ct))! >= minimumRows);
    }

    private static async Task WriteArchiveAsync(FileSystemBlobStore blobs, string path, Action<TarWriter> write)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            write(tar);
        }

        stream.Position = 0;
        await blobs.WriteAsync(path, stream, Ct);
    }

    private static void AddRaw(TarWriter tar, string name, byte[] content) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });

    private static void AddFile(TarWriter tar, string name, string content) => AddRaw(tar, name, Encoding.UTF8.GetBytes(content));

    private static BackupManifestEntry ManifestEntry(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new BackupManifestEntry(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void AddManifest(TarWriter tar, params BackupManifestEntry[] entries) =>
        AddRaw(tar, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(new BackupManifest(DateTimeOffset.UnixEpoch, entries), JsonOptions));

    private static MemoryStream CreateTamperedArchive(BackupManifest manifest)
    {
        var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            using var writer = new TarWriter(gzip, leaveOpen: true);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "agent/state.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("jello")) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)) });
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public int Count(string fragment)
        {
            lock (_messages)
            {
                return _messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>Lets a test intercept uploads (to block, fail or observe them).</summary>
    private sealed class HookedBlobStore(IBlobStore inner) : IBlobStore
    {
        public Func<CancellationToken, Task>? BeforeWrite { get; init; }

        public ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);

        public async ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
        {
            if (BeforeWrite is { } hook)
            {
                await hook(cancellationToken);
            }

            await inner.WriteAsync(path, content, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string? prefix = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
    }
    /// <summary>Records the permissions of the staged archive and its directory at upload time.</summary>
    private sealed class SpyBlobStore(IBlobStore inner) : IBlobStore
    {
        public string StagedArchivePath { get; private set; } = string.Empty;

        public UnixFileMode StagedArchiveMode { get; private set; }

        public UnixFileMode StagedDirectoryMode { get; private set; }

        public ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);

        public ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
        {
            if (content is FileStream file && !OperatingSystem.IsWindows())
            {
                StagedArchivePath = file.Name;
                StagedArchiveMode = File.GetUnixFileMode(file.Name);
                StagedDirectoryMode = File.GetUnixFileMode(Path.GetDirectoryName(file.Name)!);
            }

            return inner.WriteAsync(path, content, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string? prefix = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
    }
}
