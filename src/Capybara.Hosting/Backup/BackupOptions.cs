namespace Capybara.Hosting.Backup;

/// <summary>How a <see cref="BackupSource"/> is archived (FR-HST-05).</summary>
public enum BackupSourceKind
{
    /// <summary>Plain files or a directory tree. SQLite databases found inside (by header) are still snapshotted consistently.</summary>
    Files,

    /// <summary>A single SQLite database file, snapshotted with the SQLite online-backup API (WAL-safe).</summary>
    SqliteDatabase,
}

/// <summary>A named file or directory included in a backup archive under the top-level folder <paramref name="Name"/> (FR-HST-05).</summary>
public sealed record BackupSource(string Name, string Path, BackupSourceKind Kind = BackupSourceKind.Files);

/// <summary>
/// What to back up and where to stage it (FR-HST-05): each agent's COPILOT_HOME, the memory repo, SQLite stores and
/// encrypted credential blobs are passed as <see cref="Sources"/>. Archives are written to the blob store under <see cref="Prefix"/>.
/// </summary>
/// <param name="Sources">Sources to archive; names must be unique single path segments.</param>
/// <param name="Prefix">Blob-store prefix for archives.</param>
/// <param name="WorkingDirectory">
/// Scratch directory for staging; each run gets its own private (0700 on Unix) subdirectory. Defaults to a private temp subdirectory.
/// Always excluded from the archive.
/// </param>
/// <param name="RetainCount">Number of newest archives kept under <paramref name="Prefix"/>; older ones are pruned after a successful backup. Must be at least 1.</param>
/// <param name="ExcludedDirectories">Directories never archived even when they sit under a source (for example the blob-store root).</param>
public sealed record BackupOptions(
    IReadOnlyList<BackupSource> Sources,
    string Prefix = "backups",
    string? WorkingDirectory = null,
    int RetainCount = 14,
    IReadOnlyList<string>? ExcludedDirectories = null);
