using Capybara.Contracts.Agents;
using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Safety;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;

namespace Capybara.Memory.Admin;

/// <summary>
/// Human-only PWA edit, delete and quarantine review operations over any scope — <c>/user</c>, <c>/agents/*</c> and
/// <c>/projects/*</c> — routed through the store (size cap, history, index) and versioning (FR-MEM-10, FR-MEM-12).
/// </summary>
public sealed class MemoryAdmin
{
    private readonly WikiStore _store;
    private readonly IMemoryVersioning _versioning;

    public MemoryAdmin(WikiStore store)
        : this(store, new NoMemoryVersioning())
    {
    }

    public MemoryAdmin(WikiStore store, IMemoryVersioning versioning)
    {
        _store = store;
        _versioning = versioning;
    }

    public async Task EditPageAsync(MemoryAdminPrincipal principal, string rawPath, string body, string description, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = ParsePath(rawPath);
        await _store.WriteHumanPageAsync(path, description, body, cancellationToken);
        await _versioning.CommitAsync($"human edit {path}", cancellationToken);
    }

    /// <summary>
    /// Saves <paramref name="body"/> as the page's content, keeping its description (a new page is described by its
    /// file name). Frontmatter is never taken from the caller, so a human edit cannot forge provenance.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rawPath"/> is not a valid memory path.</exception>
    public async Task SavePageAsync(MemoryAdminPrincipal principal, string rawPath, string body, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = ParsePath(rawPath);
        var file = _store.ResolveFile(path);
        var description = File.Exists(file) && FrontmatterSerializer.TryParse(await File.ReadAllTextAsync(file, cancellationToken), out var current) && current.Metadata.Description.Length > 0
            ? current.Metadata.Description
            : Path.GetFileNameWithoutExtension(path.Value);
        await EditPageAsync(principal, path.Value, body, description, cancellationToken);
    }

    /// <summary>Returns whether the page existed.</summary>
    public async Task<bool> DeletePageAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = ParsePath(rawPath);
        if (!await _store.DeleteHumanPageAsync(path, cancellationToken))
        {
            return false;
        }

        await _versioning.CommitAsync($"human delete {path}", cancellationToken);
        return true;
    }

    /// <summary>
    /// Every page whose path starts with <paramref name="prefix"/>, in every scope and including quarantined pages:
    /// those are exactly what a human has to review (FR-MEM-12).
    /// </summary>
    public IReadOnlyList<MemoryPageInfo> ListPages(MemoryAdminPrincipal principal, string? prefix = null)
    {
        EnsureHuman(principal);
        return [.. _store.AllPaths()
            .Where(p => string.IsNullOrEmpty(prefix) || p.Value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => (Path: p, File: new FileInfo(_store.ResolveFile(p))))
            .Where(p => p.File.Exists)
            .OrderBy(p => p.Path.Value, StringComparer.Ordinal)
            .Select(p => new MemoryPageInfo(p.Path.Value, p.File.Length, new DateTimeOffset(p.File.LastWriteTimeUtc, TimeSpan.Zero)))];
    }

    /// <summary>
    /// The page's body for a human to read or edit (the raw file when its frontmatter does not parse, so a quarantined
    /// page can still be reviewed), or <see langword="null"/> when there is no such page.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rawPath"/> is not a valid memory path.</exception>
    public async Task<string?> ReadPageAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var file = _store.ResolveFile(ParsePath(rawPath));
        return File.Exists(file) ? FrontmatterSerializer.BodyOrRaw(await File.ReadAllTextAsync(file, cancellationToken)) : null;
    }

    public async Task<bool> ReleaseQuarantineAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = MemoryPath.Parse(rawPath);

        // The store's resolver rejects traversal and symlinks and creates nothing on disk.
        var file = _store.ResolveFile(path);
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
        _ = await _store.PeekAsync(new MemoryAccessContext
        {
            Agent = AgentId.Parse(agent),
            SessionId = "human:pwa",
            ProjectId = path.Scope == MemoryScopeKind.Project ? path.Owner : null,
            CanReadUser = true,
        }, path.Value, cancellationToken);
        await _versioning.CommitAsync($"release quarantine {path}", cancellationToken);
        return !_store.Quarantined.Contains(path.Value, StringComparer.Ordinal);
    }

    /// <summary>Staged changes awaiting review, oldest first, with full before/after content so a UI can render the diff.</summary>
    /// <exception cref="MemoryAccessDeniedException">The principal is not human.</exception>
    public Task<IReadOnlyList<PendingUserChange>> ListPendingAsync(MemoryAdminPrincipal principal)
    {
        EnsureHuman(principal);
        return Task.FromResult(_store.PendingUserChanges);
    }

    /// <summary>
    /// Approves (<paramref name="apply"/>) or discards a staged change. Agents can stage changes but never resolve them.
    /// </summary>
    /// <exception cref="MemoryAccessDeniedException">The principal is not human.</exception>
    public async Task<PendingChangeResult> ResolveChangeAsync(MemoryAdminPrincipal principal, string changeId, bool apply, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        return await _store.ResolveUserChangeAsync(changeId, apply, cancellationToken);
    }

    private static MemoryPath ParsePath(string rawPath) => MemoryPath.Parse(rawPath);

    private static void EnsureHuman(MemoryAdminPrincipal principal)
    {
        if (principal != MemoryAdminPrincipal.Human)
        {
            throw new MemoryAccessDeniedException("Only a human principal may administer memory.");
        }
    }
}
