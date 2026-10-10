using System.IO.Compression;
using Capybara.Memory.Store;

namespace Capybara.Memory.Export;

/// <summary>Exports the wiki for the user while excluding git internals (FR-MEM-12, NFR-06).</summary>
public sealed class MemoryExporter(MemoryOptions options)
{
    public async Task ExportZipAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var root = Path.GetFullPath(options.Root);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals(".git", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream, cancellationToken);
        }
    }
}
