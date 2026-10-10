using Fleet.Contracts.Storage;

namespace Fleet.Hosting.Storage;

/// <summary>Root-confined filesystem blob store with traversal, symlink, and atomic-write protections (FR-HST-06).</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;

    public FileSystemBlobStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Resolve(path, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        RejectSymlink(fullPath);
        var memory = new MemoryStream();
        await using (var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            await file.CopyToAsync(memory, cancellationToken);
        }

        memory.Position = 0;
        return memory;
    }

    public async ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fullPath = Resolve(path, mustExist: false);
        var directory = Path.GetDirectoryName(fullPath) ?? _root;
        Directory.CreateDirectory(directory);
        RejectSymlink(directory);
        if (File.Exists(fullPath))
        {
            RejectSymlink(fullPath);
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public ValueTask<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Resolve(path, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return ValueTask.FromResult(false);
        }

        RejectSymlink(fullPath);
        File.Delete(fullPath);
        return ValueTask.FromResult(true);
    }

    public async IAsyncEnumerable<string> ListAsync(string? prefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = prefix is null ? _root : Resolve(prefix, mustExist: false);
        if (File.Exists(start))
        {
            yield return ToRelative(start);
            yield break;
        }

        if (!Directory.Exists(start))
        {
            yield break;
        }

        RejectSymlink(start);
        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectSymlink(file);
            yield return ToRelative(file);
            await Task.Yield();
        }
    }

    private string Resolve(string relativePath, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Blob paths must be relative forward-slash paths without traversal.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(fullPath, _root, StringComparison.Ordinal))
        {
            throw new ArgumentException("Blob path escapes the store root.", nameof(relativePath));
        }

        if (mustExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("Blob path not found.", fullPath);
        }

        RejectSymlinkAncestors(fullPath);
        return fullPath;
    }

    private string ToRelative(string fullPath) => Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static void RejectSymlinkAncestors(string path)
    {
        var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectSymlink(current);
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void RejectSymlink(string path)
    {
        var info = File.GetAttributes(path);
        if ((info & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Symlinks are not allowed in the blob store.");
        }
    }
}
