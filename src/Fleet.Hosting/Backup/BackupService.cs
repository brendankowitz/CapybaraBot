using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Fleet.Contracts.Storage;

namespace Fleet.Hosting.Backup;

/// <summary>
/// Creates and restores integrity-checked tar.gz backups in an <c>IBlobStore</c> (FR-HST-05).
/// Each archive holds every source file under <c>{source name}/...</c> plus a trailing <c>manifest.json</c> with SHA-256 hashes.
/// Symlinks are skipped. Restore verifies the archive against its manifest before writing anything to the destination.
/// </summary>
public sealed class BackupService
{
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IBlobStore _blobs;
    private readonly TimeProvider _timeProvider;
    private readonly BackupOptions _options;

    public BackupService(IBlobStore blobs, TimeProvider timeProvider, BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.AsSpan().IndexOfAny('/', '\\') >= 0 || source.Name is "." or ".." || !names.Add(source.Name))
            {
                throw new ArgumentException($"Backup source name '{source.Name}' must be a unique single path segment.", nameof(options));
            }
        }

        _blobs = blobs;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>Archives all sources into the blob store and returns the archive's blob path and manifest.</summary>
    /// <exception cref="FileNotFoundException">A configured source does not exist.</exception>
    public async Task<BackupResult> BackupAsync(CancellationToken cancellationToken = default)
    {
        var createdAt = _timeProvider.GetUtcNow();
        var blobPath = $"{_options.Prefix.TrimEnd('/')}/{createdAt:yyyyMMddTHHmmssfffZ}.tar.gz";
        var workDir = EnsureWorkingDirectory();
        var archivePath = Path.Combine(workDir, $"backup-{Guid.NewGuid():N}.tar.gz");
        try
        {
            var entries = new List<BackupManifestEntry>();
            await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var source in _options.Sources)
                {
                    foreach (var (fullPath, archiveName) in EnumerateSource(source))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entries.Add(await AddFileAsync(tar, fullPath, archiveName, cancellationToken));
                    }
                }

                var manifest = new BackupManifest(createdAt, entries);
                await using var manifestStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestName) { DataStream = manifestStream }, cancellationToken);
            }

            await using (var upload = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                await _blobs.WriteAsync(blobPath, upload, cancellationToken);
            }

            return new BackupResult(blobPath, new BackupManifest(createdAt, entries));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    /// <summary>Verifies the archive at <paramref name="blobPath"/> against its manifest, then extracts it into <paramref name="destination"/>.</summary>
    /// <exception cref="FileNotFoundException">The archive does not exist in the blob store.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed, contains unsafe paths, or does not match its manifest (tampering or corruption).</exception>
    public async Task RestoreAsync(string blobPath, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        await using var archive = await _blobs.OpenReadAsync(blobPath, cancellationToken)
            ?? throw new FileNotFoundException("Backup archive not found.", blobPath);

        var staging = Path.Combine(EnsureWorkingDirectory(), $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var actual = new Dictionary<string, BackupManifestEntry>(StringComparer.Ordinal);
            BackupManifest? manifest = null;
            await using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
            await using (var reader = new TarReader(gzip))
            {
                while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                    {
                        throw new InvalidDataException($"Unexpected archive entry '{entry.Name}'.");
                    }

                    if (entry.Name == ManifestName)
                    {
                        manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(entry.DataStream, JsonOptions, cancellationToken)
                            ?? throw new InvalidDataException("Backup manifest is empty.");
                        continue;
                    }

                    var target = ResolveInside(staging, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!actual.TryAdd(entry.Name, await ExtractAsync(entry.DataStream, target, entry.Name, cancellationToken)))
                    {
                        throw new InvalidDataException($"Duplicate archive entry '{entry.Name}'.");
                    }
                }
            }

            VerifyAgainstManifest(manifest, actual);
            Directory.CreateDirectory(destination);
            foreach (var name in actual.Keys)
            {
                var finalPath = ResolveInside(Path.GetFullPath(destination), name);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.Move(ResolveInside(staging, name), finalPath, overwrite: true);
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static void VerifyAgainstManifest(BackupManifest? manifest, Dictionary<string, BackupManifestEntry> actual)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("Backup archive has no manifest.");
        }

        if (manifest.Entries.Count != actual.Count)
        {
            throw new InvalidDataException("Backup archive contents do not match its manifest.");
        }

        foreach (var expected in manifest.Entries)
        {
            if (!actual.TryGetValue(expected.Path, out var found) || found.Length != expected.Length || !string.Equals(found.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup entry '{expected.Path}' failed integrity verification.");
            }
        }
    }

    private static async Task<BackupManifestEntry> ExtractAsync(Stream data, string target, string name, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            length += read;
        }

        return new BackupManifestEntry(name, length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task<BackupManifestEntry> AddFileAsync(TarWriter tar, string fullPath, string archiveName, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
        using var buffered = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            buffered.Write(buffer, 0, read);
        }

        // Buffer so the hash and length always describe exactly the bytes written even if the live file changes mid-backup.
        buffered.Position = 0;
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, archiveName) { DataStream = buffered }, cancellationToken);
        return new BackupManifestEntry(archiveName, buffered.Length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static IEnumerable<(string FullPath, string ArchiveName)> EnumerateSource(BackupSource source)
    {
        var root = Path.GetFullPath(source.Path);
        if (File.Exists(root))
        {
            yield return (root, $"{source.Name}/{Path.GetFileName(root)}");
            yield break;
        }

        if (!Directory.Exists(root))
        {
            throw new FileNotFoundException($"Backup source '{source.Name}' not found.", root);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return (file, $"{source.Name}/{relative}");
        }
    }

    private static string ResolveInside(string root, string archiveName)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, archiveName));
        if (Path.IsPathRooted(archiveName) || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry '{archiveName}' escapes the restore directory.");
        }

        return full;
    }

    private string EnsureWorkingDirectory()
    {
        var dir = Path.GetFullPath(_options.WorkingDirectory ?? Path.Combine(Path.GetTempPath(), "fleet-backup"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
