using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Store;

namespace Capybara.Memory.Abstractions;

/// <summary>
/// Host-owned memory operations exposed to runtime tools (FR-MEM-02, FR-MEM-03, FR-MEM-05, FR-MEM-12). Deliberately has no
/// way to list or resolve staged changes: approval is human-only and lives on <c>MemoryAdmin</c>.
/// </summary>
public interface IMemoryStore
{
    Task<MemoryPage?> ReadAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    IReadOnlyList<string> List(MemoryAccessContext context, string? prefix = null);

    Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit = 10, CancellationToken cancellationToken = default);

    Task<string> BuildIndexAsync(MemoryAccessContext context, CancellationToken cancellationToken = default);

    Task<MemoryOperation> RememberAsync(MemoryAccessContext context, string rawPath, string fact, string description, CancellationToken cancellationToken = default);

    Task<MemoryOperation> SupersedeAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    Task<MemoryOperation> ForgetAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);

    /// <summary>Records that the agent re-verified the page against its source system, setting <c>lastValidated</c> (FR-MEM-04, FR-MEM-07).</summary>
    Task<MemoryOperation> MarkValidatedAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default);
}
