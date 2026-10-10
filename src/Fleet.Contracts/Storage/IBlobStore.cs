namespace Fleet.Contracts.Storage;

/// <summary>
/// Blob storage for artifacts, evidence and backups (FR-HST-06): filesystem now, Azure Files or Blob later.
/// Paths are relative, forward-slash separated and must not escape the store root.
/// </summary>
public interface IBlobStore
{
    ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
