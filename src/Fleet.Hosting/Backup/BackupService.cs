using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using Fleet.Contracts.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Hosting.Backup;

/// <summary>
/// Creates and restores integrity-checked tar.gz backups in an <c>IBlobStore</c> (FR-HST-05).
/// Each archive holds every source file under <c>{source name}/...</c> plus a trailing <c>manifest.json</c> with SHA-256 hashes and Unix modes.
/// SQLite databases (explicit <see cref="BackupSourceKind.SqliteDatabase"/> sources, or files with a SQLite header inside directory sources)
/// are archived as consistent online-backup snapshots so WAL-mode stores are never torn; their <c>-wal</c>/<c>-shm</c>/<c>-journal</c> files are skipped.
/// Symlinks are skipped. Restore verifies the archive against its manifest before writing anything to the destination;
/// that detects corruption and truncation but does not authenticate the archive (see <see cref="BackupManifest"/>).
/// Staging happens in a per-run private directory, one file at a time, so memory use does not depend on file size.
/// </summary>
public sealed partial class BackupService
{
    private const string ManifestName = "manifest.json";
    private const string ArchiveSuffix = ".tar.gz";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const int BufferSize = 81920;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SqliteExtensions = [".db", ".sqlite", ".sqlite3"];
    private static readonly string[] SqliteSidecarSuffixes = ["-wal", "-shm", "-journal"];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IBlobStore _blobs;
    private readonly TimeProvider _timeProvider;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _logger;

    public BackupService(IBlobStore blobs, TimeProvider timeProvider, BackupOptions options, ILogger<BackupService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainCount, 1);
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
        _logger = logger ?? NullLogger<BackupService>.Instance;
    }

    /// <summary>
    /// Archives all sources into the blob store, then prunes archives beyond <see cref="BackupOptions.RetainCount"/>.
    /// Files that vanish between enumeration and reading are skipped (and logged); a missing configured source is an error.
    /// </summary>
    /// <exception cref="FileNotFoundException">A configured source does not exist.</exception>
    public async Task<BackupResult> BackupAsync(CancellationToken cancellationToken = default)
    {
        var createdAt = _timeProvider.GetUtcNow();
        var blobPath = $"{_options.Prefix.TrimEnd('/')}/{createdAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)}{ArchiveSuffix}";
        var runDir = CreateRunDirectory();
        try
        {
            var archivePath = Path.Combine(runDir, "backup" + ArchiveSuffix);
            var excluded = ExcludedRoots(runDir);
            var entries = new List<BackupManifestEntry>();
            await using (var file = CreatePrivateFile(archivePath))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var source in _options.Sources)
                {
                    foreach (var sourceFile in EnumerateSource(source, excluded))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (await TryAddFileAsync(tar, sourceFile, runDir, cancellationToken) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                }

                var manifest = new BackupManifest(createdAt, entries);
                await using var manifestStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestName) { DataStream = manifestStream }, cancellationToken);
            }

            await using (var upload = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _blobs.WriteAsync(blobPath, upload, cancellationToken);
            }

            await PruneAsync(cancellationToken);
            return new BackupResult(blobPath, new BackupManifest(createdAt, entries));
        }
        finally
        {
            Directory.Delete(runDir, recursive: true);
        }
    }

    /// <summary>The creation time of the newest archive under the configured prefix, or <see langword="null"/> when there is none.</summary>
    public async Task<DateTimeOffset?> GetLastBackupTimeAsync(CancellationToken cancellationToken = default)
    {
        var archives = await ListArchivesAsync(cancellationToken);
        return archives.Count == 0 ? null : archives[0].CreatedAt;
    }

    /// <summary>Verifies the archive at <paramref name="blobPath"/> against its manifest, then extracts it into <paramref name="destination"/>.</summary>
    /// <exception cref="FileNotFoundException">The archive does not exist in the blob store.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed, contains unsafe paths or non-file entries, or does not match its manifest (corruption).</exception>
    public async Task RestoreAsync(string blobPath, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        await using var archive = await _blobs.OpenReadAsync(blobPath, cancellationToken)
            ?? throw new FileNotFoundException("Backup archive not found.", blobPath);

        var runDir = CreateRunDirectory();
        try
        {
            var staging = Path.Combine(runDir, "files");
            Directory.CreateDirectory(staging);
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
                        manifest = manifest is null
                            ? await ReadManifestAsync(entry.DataStream, cancellationToken)
                            : throw new InvalidDataException("Backup archive has more than one manifest.");
                        continue;
                    }

                    ValidateEntryName(entry.Name);
                    var target = ResolveInside(staging, entry.Name);
                    if (actual.ContainsKey(entry.Name) || File.Exists(target))
                    {
                        throw new InvalidDataException($"Duplicate archive entry '{entry.Name}'.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    actual.Add(entry.Name, await ExtractAsync(entry.DataStream, target, entry.Name, cancellationToken));
                }
            }

            var expected = VerifyAgainstManifest(manifest, actual);
            var destinationFull = Path.GetFullPath(destination);
            Directory.CreateDirectory(destinationFull);
            foreach (var (name, entry) in expected)
            {
                var finalPath = ResolveInside(destinationFull, name);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (entry.IsSqlite)
                {
                    DeleteStaleSqliteSidecars(finalPath);
                }

                File.Move(ResolveInside(staging, name), finalPath, overwrite: true);
                ApplyUnixMode(finalPath, entry.UnixMode);
            }
        }
        finally
        {
            Directory.Delete(runDir, recursive: true);
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(Stream data, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(data, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Backup manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup manifest is not valid JSON.", ex);
        }
    }

    private static Dictionary<string, BackupManifestEntry> VerifyAgainstManifest(BackupManifest? manifest, Dictionary<string, BackupManifestEntry> actual)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("Backup archive has no manifest.");
        }

        if (manifest.Entries is null)
        {
            throw new InvalidDataException("Backup manifest has no entry list.");
        }

        var expected = new Dictionary<string, BackupManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (entry?.Path is null || !expected.TryAdd(entry.Path, entry))
            {
                throw new InvalidDataException("Backup manifest has a missing or duplicate entry.");
            }
        }

        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys))
        {
            throw new InvalidDataException("Backup archive contents do not match its manifest.");
        }

        foreach (var (path, entry) in expected)
        {
            var found = actual[path];
            if (found.Length != entry.Length || !string.Equals(found.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup entry '{path}' failed integrity verification.");
            }
        }

        return expected;
    }

    private static async Task<BackupManifestEntry> ExtractAsync(Stream data, string target, string name, CancellationToken cancellationToken)
    {
        await using var output = CreatePrivateFile(target);
        var (length, sha256) = await CopyAndHashAsync(data, output, cancellationToken);
        return new BackupManifestEntry(name, length, sha256);
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            length += read;
        }

        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private async Task<BackupManifestEntry?> TryAddFileAsync(TarWriter tar, SourceFile file, string runDir, CancellationToken cancellationToken)
    {
        var stagedPath = Path.Combine(runDir, $"stage-{Guid.NewGuid():N}");
        try
        {
            BackupManifestEntry entry;
            try
            {
                entry = await StageAsync(file, stagedPath, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                LogFileVanished(_logger, file.FullPath);
                return null;
            }

            // The tar entry streams from the staged copy, so the manifest hash/length describe exactly the archived bytes
            // even if the live file keeps changing.
            await using var data = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, file.ArchiveName) { DataStream = data };
            if (entry.UnixMode is { } mode)
            {
                tarEntry.Mode = (UnixFileMode)mode;
            }

            await tar.WriteEntryAsync(tarEntry, cancellationToken);
            return entry;
        }
        finally
        {
            File.Delete(stagedPath);
        }
    }

    private static async Task<BackupManifestEntry> StageAsync(SourceFile file, string stagedPath, CancellationToken cancellationToken)
    {
        if (file.IsSqlite)
        {
            await SnapshotSqliteAsync(file.FullPath, stagedPath, cancellationToken);
            var mode = ReadUnixMode(file.FullPath);
            await using var snapshot = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(snapshot, cancellationToken));
            return new BackupManifestEntry(file.ArchiveName, snapshot.Length, sha256, mode, IsSqlite: true);
        }

        await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var unixMode = ReadUnixMode(file.FullPath);
        await using var output = CreatePrivateFile(stagedPath);
        var (length, hash) = await CopyAndHashAsync(input, output, cancellationToken);
        return new BackupManifestEntry(file.ArchiveName, length, hash, unixMode);
    }

    // Microsoft.Data.Sqlite exposes the online-backup API synchronously only; run it off the caller's thread.
    private static Task SnapshotSqliteAsync(string source, string destination, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("SQLite database not found.", source);
                }

                using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                from.Open();
                to.Open();
                from.BackupDatabase(to);
            },
            cancellationToken);

    private static List<SourceFile> EnumerateSource(BackupSource source, IReadOnlyList<string> excludedRoots)
    {
        var root = Path.GetFullPath(source.Path);
        var isSqliteSource = source.Kind == BackupSourceKind.SqliteDatabase;
        if (File.Exists(root))
        {
            return [new SourceFile(root, $"{source.Name}/{Path.GetFileName(root)}", isSqliteSource || LooksLikeSqlite(root))];
        }

        if (isSqliteSource || !Directory.Exists(root))
        {
            throw new FileNotFoundException($"Backup source '{source.Name}' not found.", root);
        }

        // A source explicitly rooted inside an excluded directory wins over the exclusion.
        var excluded = excludedRoots.Where(e => !IsUnderOrEqual(root, e)).ToArray();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new FileSystemEnumerable<string>(root, static (ref FileSystemEntry entry) => entry.ToFullPath(), options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsExcluded(entry.ToFullPath(), excluded),
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && !IsExcluded(entry.ToFullPath(), excluded),
        }.Order(StringComparer.Ordinal).ToList();

        var sqliteFiles = files.Where(f => SqliteExtensions.Any(ext => f.EndsWith(ext, PathComparison)) && LooksLikeSqlite(f)).ToHashSet(PathComparer);
        var result = new List<SourceFile>(files.Count);
        foreach (var file in files)
        {
            if (SqliteSidecarSuffixes.FirstOrDefault(suffix => file.EndsWith(suffix, PathComparison)) is { } suffix && sqliteFiles.Contains(file[..^suffix.Length]))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(new SourceFile(file, $"{source.Name}/{relative}", sqliteFiles.Contains(file)));
        }

        return result;
    }

    private static bool LooksLikeSqlite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var old in (await ListArchivesAsync(cancellationToken)).Skip(_options.RetainCount))
            {
                await _blobs.DeleteAsync(old.Path, cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The new archive is already safely stored; a failed prune must not fail the backup.
            LogPruneFailed(_logger, ex);
        }
    }

    /// <summary>Archives directly under the prefix whose names are backup timestamps, newest first.</summary>
    private async Task<List<(string Path, DateTimeOffset CreatedAt)>> ListArchivesAsync(CancellationToken cancellationToken)
    {
        var prefix = _options.Prefix.TrimEnd('/');
        var archives = new List<(string Path, DateTimeOffset CreatedAt)>();
        await foreach (var path in _blobs.ListAsync(prefix, cancellationToken))
        {
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal)
                && path.LastIndexOf('/') == prefix.Length
                && path.EndsWith(ArchiveSuffix, StringComparison.Ordinal)
                && DateTimeOffset.TryParseExact(path[(prefix.Length + 1)..^ArchiveSuffix.Length], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
            {
                archives.Add((path, createdAt));
            }
        }

        archives.Sort(static (a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return archives;
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Split('/').Any(segment => segment is "" or "." or ".." || segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Archive entry '{name}' has an unsafe path.");
        }
    }

    private static string ResolveInside(string root, string archiveName)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(rootFull, archiveName));
        if (Path.IsPathRooted(archiveName) || !full.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"Archive entry '{archiveName}' escapes the restore directory.");
        }

        return full;
    }

    private static void DeleteStaleSqliteSidecars(string databasePath)
    {
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            File.Delete(databasePath + suffix);
        }
    }

    private static int? ReadUnixMode(string path) => OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path) & 0x1FF;

    private static void ApplyUnixMode(string path, int? mode)
    {
        // Mask to permission bits: the manifest is unauthenticated, so never restore setuid/setgid/sticky.
        if (!OperatingSystem.IsWindows() && mode is { } bits)
        {
            File.SetUnixFileMode(path, (UnixFileMode)(bits & 0x1FF));
        }
    }

    private List<string> ExcludedRoots(string runDir)
    {
        var roots = new List<string> { runDir };
        if (_options.WorkingDirectory is not null)
        {
            roots.Add(_options.WorkingDirectory);
        }

        roots.AddRange(_options.ExcludedDirectories ?? []);
        return [.. roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))];
    }

    private static bool IsExcluded(string path, string[] excludedRoots) => excludedRoots.Any(root => IsUnderOrEqual(path, root));

    private static bool IsUnderOrEqual(string path, string directory) =>
        path.Equals(directory, PathComparison)
        || (path.Length > directory.Length && path.StartsWith(directory, PathComparison) && path[directory.Length] == Path.DirectorySeparatorChar);

    /// <summary>
    /// Creates a unique, private (0700 on Unix) scratch directory for one backup or restore run, so concurrent runs never share
    /// or squat a predictable path and staged credentials are not readable by other local users.
    /// </summary>
    private string CreateRunDirectory()
    {
        if (_options.WorkingDirectory is null)
        {
            return Directory.CreateTempSubdirectory("fleet-backup-").FullName;
        }

        var root = Path.GetFullPath(_options.WorkingDirectory);
        CreatePrivateDirectory(root);
        var run = Path.Combine(root, $"run-{Guid.NewGuid():N}");
        CreatePrivateDirectory(run);
        return run;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = BufferSize, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private sealed record SourceFile(string FullPath, string ArchiveName, bool IsSqlite);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping '{Path}': it was removed while the backup was running.")]
    private static partial void LogFileVanished(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning old backups failed; the new backup was kept.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
