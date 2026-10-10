namespace Fleet.Hosting.Backup;

/// <summary>A named file or directory included in a backup archive under the top-level folder <paramref name="Name"/> (FR-HST-05).</summary>
public sealed record BackupSource(string Name, string Path);

/// <summary>
/// What to back up and where to stage it (FR-HST-05): each agent's COPILOT_HOME, the memory repo, SQLite stores and
/// encrypted credential blobs are passed as <see cref="Sources"/>. Archives are written to the blob store under <see cref="Prefix"/>.
/// </summary>
/// <param name="Sources">Sources to archive; names must be unique single path segments.</param>
/// <param name="Prefix">Blob-store prefix for archives.</param>
/// <param name="WorkingDirectory">Scratch directory for staging archives and restores; defaults to a folder under the system temp path.</param>
public sealed record BackupOptions(IReadOnlyList<BackupSource> Sources, string Prefix = "backups", string? WorkingDirectory = null);

/// <summary>How often the hosted backup worker runs (FR-HST-05).</summary>
public sealed record BackupScheduleOptions(TimeSpan Interval);
