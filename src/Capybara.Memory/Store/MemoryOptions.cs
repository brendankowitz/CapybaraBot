namespace Capybara.Memory.Store;

public sealed class MemoryOptions
{
    /// <summary>Memory root. Persona files, config, routines and skills live outside it (FR-MEM-06).</summary>
    public required string Root { get; init; }

    public int MaxPageBytes { get; init; } = 64 * 1024;

    /// <summary>Approximate token budget for the per-turn index (FR-MEM-02).</summary>
    public int IndexTokenBudget { get; init; } = 1_500;

    /// <summary>Cap for the always-injected <c>/user/profile.md</c> (~500 tokens).</summary>
    public int ProfileTokenBudget { get; init; } = 500;

    /// <summary>Use SQLite FTS5 once the wiki index is larger than this approximate token count (FR-MEM-11).</summary>
    public int FtsSearchTokenThreshold { get; init; } = 8_000;

    /// <summary>Archive pages not accessed for this many days during consolidation (FR-MEM-08).</summary>
    public int ArchiveAfterDays { get; init; } = 28;

    /// <summary>Most changes one proposer (an agent or the steward) may have waiting for human review at once (FR-MEM-05).</summary>
    public int MaxPendingPerProposer { get; init; } = 50;

    /// <summary>A read refreshes a page's <c>lastAccessed</c> only when the stored value is older than this (FR-MEM-04).</summary>
    public TimeSpan LastAccessedDebounce { get; init; } = TimeSpan.FromHours(1);
}
