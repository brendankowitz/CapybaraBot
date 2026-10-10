using Capybara.Contracts.Agents;
using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Safety;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;

namespace Capybara.Memory.Admin;

/// <summary>Human-only PWA edit, delete and quarantine review operations routed through versioning (FR-MEM-10, FR-MEM-12).</summary>
public sealed class MemoryAdmin
{
    private readonly WikiStore _store;
    private readonly TimeProvider _time;
    private readonly IMemoryVersioning _versioning;

    public MemoryAdmin(WikiStore store, TimeProvider time)
        : this(store, time, new NoMemoryVersioning())
    {
    }

    public MemoryAdmin(WikiStore store, TimeProvider time, IMemoryVersioning versioning)
    {
        _store = store;
        _time = time;
        _versioning = versioning;
    }

    public async Task EditPageAsync(MemoryAdminPrincipal principal, string rawPath, string body, string description, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = EnsureUserPath(rawPath);
        var file = ResolveFile(path);
        var now = _time.GetUtcNow();
        PageMetadata metadata;
        if (File.Exists(file))
        {
            var (existing, _) = FrontmatterSerializer.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            metadata = existing with { Description = description, Updated = now, Source = "user:pwa" };
        }
        else
        {
            metadata = new PageMetadata { Description = description, Source = "user:pwa", Created = now, Updated = now, ValidFrom = now };
        }

        await WriteFileAsync(file, FrontmatterSerializer.Write(metadata, body), cancellationToken);
        await _versioning.CommitAsync($"human edit {path}", cancellationToken);
    }

    public async Task DeletePageAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = EnsureUserPath(rawPath);
        var file = ResolveFile(path);
        if (File.Exists(file))
        {
            File.Delete(file);
            await _versioning.CommitAsync($"human delete {path}", cancellationToken);
        }
    }

    public async Task<bool> ReleaseQuarantineAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = MemoryPath.Parse(rawPath);
        var file = ResolveFile(path);
        if (!File.Exists(file))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(file, cancellationToken);
        if (InjectionScanner.LooksInjected(content))
        {
            throw new MemoryAccessDeniedException("Quarantined page still contains injection markers.");
        }

        var agent = path.Scope == MemoryScopeKind.Agent && path.Owner is not null ? path.Owner : "admin";
        _ = await _store.ReadAsync(new MemoryAccessContext
        {
            Agent = AgentId.Parse(agent),
            SessionId = "human:pwa",
            ProjectId = path.Scope == MemoryScopeKind.Project ? path.Owner : null,
            CanReadUser = true,
        }, path.Value, cancellationToken);
        await _versioning.CommitAsync($"release quarantine {path}", cancellationToken);
        return !_store.Quarantined.Contains(path.Value, StringComparer.Ordinal);
    }

    private static MemoryPath EnsureUserPath(string rawPath)
    {
        var path = MemoryPath.Parse(rawPath);
        return path.Scope == MemoryScopeKind.User
            ? path
            : throw new MemoryAccessDeniedException("PWA admin operations are restricted to /user pages.");
    }

    private string ResolveFile(MemoryPath path)
    {
        var root = _store.Root;
        var full = Path.GetFullPath(Path.Combine(root, path.RelativeFileSystemPath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MemoryAccessDeniedException("Path escapes the memory root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static async Task WriteFileAsync(string file, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, file, overwrite: true);
    }

    private static void EnsureHuman(MemoryAdminPrincipal principal)
    {
        if (principal != MemoryAdminPrincipal.Human)
        {
            throw new MemoryAccessDeniedException("Only a human principal may administer user memory.");
        }
    }
}
