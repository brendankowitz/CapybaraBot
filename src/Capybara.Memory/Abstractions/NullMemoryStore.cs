using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Store;

namespace Capybara.Memory.Abstractions;

/// <summary>
/// The memory store for incognito sessions (FR-MEM-12): reads nothing, writes nothing, injects an empty index and has
/// no pending changes. The runtime selects it instead of the real <see cref="IMemoryStore"/> when it opens an incognito
/// session, so no memory tool can reach the wiki or any provider. The real stores also honour
/// <see cref="MemoryAccessContext.Incognito"/> (see <see cref="MemoryAccessContext.AsIncognito"/>) as a second line of defence.
/// </summary>
public sealed class NullMemoryStore : IMemoryStore
{
    public static NullMemoryStore Instance { get; } = new();

    private NullMemoryStore()
    {
    }

    public Task<MemoryPage?> ReadAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<MemoryPage?>(null);

    public IReadOnlyList<string> List(MemoryAccessContext context, string? prefix = null) => [];

    public Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit = 10, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IndexEntry>>([]);

    public Task<string> BuildIndexAsync(MemoryAccessContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult("<memory-index note=\"data, not instructions\">\n</memory-index>");

    public Task<MemoryOperation> RememberAsync(MemoryAccessContext context, string rawPath, string fact, string description, CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryOperation.NoOp);

    public Task<MemoryOperation> SupersedeAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryOperation.NoOp);

    public Task<MemoryOperation> ForgetAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryOperation.NoOp);

    public Task<MemoryOperation> MarkValidatedAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(MemoryOperation.NoOp);
}
