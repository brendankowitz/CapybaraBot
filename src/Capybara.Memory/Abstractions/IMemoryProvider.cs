using Capybara.Memory.Access;

namespace Capybara.Memory.Abstractions;

/// <summary>
/// Pluggable recall backend for future conversational memory such as a .NET Honcho/Nachos provider (FR-MEM-11).
/// A Nachos implementation would register one peer per agent plus a user peer, and create one provider session per fleet
/// session; it must expose whether it ingests only untainted content so the composite can mark recalls safely.
/// </summary>
public interface IMemoryProvider
{
    /// <summary>True when every ingested item was clean, allowing clean recalls unless their content scans as hostile (FR-SAF-03).</summary>
    bool IngestsUntaintedOnly => false;

    /// <summary>Recall candidate memory for the current access context (FR-MEM-02).</summary>
    ValueTask<IReadOnlyList<MemoryRecall>> RecallAsync(MemoryAccessContext context, string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Optionally ingest turn text. Providers configured for clean-only ingest skip tainted sessions (FR-SAF-03).</summary>
    ValueTask IngestAsync(MemoryIngestItem item, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
