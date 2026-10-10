using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Fleet.Contracts.Storage;

namespace Fleet.Hosting.Storage;

/// <summary>Azure Blob implementation for durable artifacts and backups (FR-HST-06, FR-HST-07).</summary>
public sealed class AzureBlobStore(BlobContainerClient container) : IBlobStore
{
    private readonly BlobContainerClient _container = container;

    public async ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        Validate(path);
        var blob = _container.GetBlobClient(path);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var memory = new MemoryStream();
        await blob.DownloadToAsync(memory, cancellationToken);
        memory.Position = 0;
        return memory;
    }

    public async ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        Validate(path);
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await _container.GetBlobClient(path).UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Validate(path);
        var response = await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        return response.Value;
    }

    public async IAsyncEnumerable<string> ListAsync(string? prefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            yield return blob.Name;
        }
    }

    private static void Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] == '/' || path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob paths must be relative and non-traversing.", nameof(path));
        }
    }
}
