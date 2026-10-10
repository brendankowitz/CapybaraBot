using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fleet.Hosting.Backup;
using Fleet.Hosting.Storage;

namespace Fleet.Hosting.Tests;

public sealed class BackupTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Backup_restore_roundtrips_and_detects_tamper()
    {
        using var source = new LocalTempDirectory();
        using var blobs = new LocalTempDirectory();
        using var restore = new LocalTempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "agent"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, "agent", "state.txt"), "hello", TestContext.Current.CancellationToken);
        var blobStore = new FileSystemBlobStore(blobs.Path);
        var service = new BackupService(blobStore, TimeProvider.System, new BackupOptions([new BackupSource("agent", Path.Combine(source.Path, "agent"))], WorkingDirectory: Path.Combine(source.Path, "work")));

        var result = await service.BackupAsync(TestContext.Current.CancellationToken);
        await service.RestoreAsync(result.Path, restore.Path, TestContext.Current.CancellationToken);

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(restore.Path, "agent", "state.txt"), TestContext.Current.CancellationToken));

        await blobStore.WriteAsync("backups/tampered.tar.gz", CreateTamperedArchive(result.Manifest), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.RestoreAsync("backups/tampered.tar.gz", restore.Path, TestContext.Current.CancellationToken));
    }

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
}
