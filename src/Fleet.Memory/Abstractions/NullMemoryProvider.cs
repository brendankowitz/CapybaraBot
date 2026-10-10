using Fleet.Memory.Access;

namespace Fleet.Memory.Abstractions;

/// <summary>No-op provider used when no external recall backend is configured (FR-MEM-11).</summary>
public sealed class NullMemoryProvider : IMemoryProvider
{
    public ValueTask<IReadOnlyList<MemoryRecall>> RecallAsync(MemoryAccessContext context, string query, int limit, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryRecall>>([]);
}
