using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Store;

namespace Capybara.Memory.Abstractions;

/// <summary>Host-owned memory operations exposed to runtime tools (FR-MEM-02, FR-MEM-03, FR-MEM-05, FR-MEM-12).</summary>
public interface IMemoryStore
{
    IReadOnlyList<PendingUserChange> PendingUserChanges { get; }

    Task<MemoryPage?> ReadAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    IReadOnlyList<string> List(MemoryAccessContext context, string? prefix = null);

    Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit = 10, CancellationToken cancellationToken = default);

    Task<string> BuildIndexAsync(MemoryAccessContext context, CancellationToken cancellationToken = default);

    Task<MemoryOperation> RememberAsync(MemoryAccessContext context, string rawPath, string fact, string description, CancellationToken cancellationToken = default);

    Task<MemoryOperation> SupersedeAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    Task<MemoryOperation> ForgetAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    Task<bool> ResolveUserChangeAsync(string changeId, bool apply, CancellationToken cancellationToken = default);
}
