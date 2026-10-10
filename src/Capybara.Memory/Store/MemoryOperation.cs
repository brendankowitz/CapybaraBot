namespace Capybara.Memory.Store;

/// <summary>The merge decision taken by <c>remember</c> (FR-MEM-03).</summary>
public enum MemoryOperation
{
    Add,
    Update,
    Delete,
    NoOp,

    /// <summary>The change touches <c>/user/</c> and was staged for human review.</summary>
    Proposed,
}
