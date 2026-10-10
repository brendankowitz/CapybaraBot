using Capybara.Contracts.Agents;
using Capybara.Memory.Access;
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

    public async Task DeletePageAsync(MemoryAdminPrincipal principal, string rawPath, CancellationToken cancellationToken = default)
    {
        EnsureHuman(principal);
        var path = ParsePath(rawPath);
        if (await _store.DeleteHumanPageAsync(path, cancellationToken))
        {
            await _versioning.CommitAsync($"human delete {path}", cancellationToken);
        }
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
