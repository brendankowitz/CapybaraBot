namespace Fleet.Memory.Store;

public sealed class MemoryOptions
{
    /// <summary>Memory root. Persona files, config, routines and skills live outside it (FR-MEM-06).</summary>
    public required string Root { get; init; }

    public int MaxPageBytes { get; init; } = 64 * 1024;

    /// <summary>Approximate token budget for the per-turn index (FR-MEM-02).</summary>
    public int IndexTokenBudget { get; init; } = 1_500;

    /// <summary>Cap for the always-injected <c>/user/profile.md</c> (~500 tokens).</summary>
    public int ProfileTokenBudget { get; init; } = 500;
}
