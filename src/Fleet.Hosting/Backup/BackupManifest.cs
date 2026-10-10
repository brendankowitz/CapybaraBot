namespace Fleet.Hosting.Backup;

/// <summary>One archived file: archive-relative forward-slash path, byte length and lowercase hex SHA-256 (FR-HST-05).</summary>
public sealed record BackupManifestEntry(string Path, long Length, string Sha256);

/// <summary>Integrity manifest stored as <c>manifest.json</c> inside each backup archive; restore refuses archives that do not match it (FR-HST-05).</summary>
public sealed record BackupManifest(DateTimeOffset CreatedAt, IReadOnlyList<BackupManifestEntry> Entries);

/// <summary>Outcome of <see cref="BackupService.BackupAsync"/>: the blob path of the archive and its manifest.</summary>
public sealed record BackupResult(string Path, BackupManifest Manifest);
