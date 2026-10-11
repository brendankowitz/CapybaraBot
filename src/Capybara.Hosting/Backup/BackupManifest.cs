namespace Capybara.Hosting.Backup;

/// <summary>
/// One archived file (FR-HST-05): archive-relative forward-slash path, byte length and lowercase hex SHA-256 of the archived bytes.
/// </summary>
/// <param name="Path">Archive-relative path.</param>
/// <param name="Length">Byte length of the archived content.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the archived content.</param>
/// <param name="UnixMode">Source file permission bits (0-0777) on Unix; <see langword="null"/> when captured on Windows. Re-applied on restore.</param>
/// <param name="IsSqlite">The entry is a consistent snapshot of a SQLite database; restore removes stale <c>-wal</c>/<c>-shm</c>/<c>-journal</c> files beside it.</param>
public sealed record BackupManifestEntry(string Path, long Length, string Sha256, int? UnixMode = null, bool IsSqlite = false);

/// <summary>
/// Manifest stored as <c>manifest.json</c> inside each backup archive (FR-HST-05). Restore refuses archives whose contents do not match it,
/// which detects corruption and truncation. The manifest is <em>not</em> authenticated: someone able to write to the blob store
/// can forge a self-consistent archive, so protect the blob store itself.
/// </summary>
public sealed record BackupManifest(DateTimeOffset CreatedAt, IReadOnlyList<BackupManifestEntry> Entries);

/// <summary>Outcome of <see cref="BackupService.BackupAsync"/>: the blob path of the archive and its manifest.</summary>
public sealed record BackupResult(string Path, BackupManifest Manifest);
