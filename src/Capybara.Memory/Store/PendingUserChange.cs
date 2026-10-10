namespace Capybara.Memory.Store;

/// <summary>What applying a <see cref="PendingUserChange"/> does to the wiki.</summary>
public enum PendingChangeKind
{
    /// <summary>Replace (or create) the page at <see cref="PendingUserChange.Path"/> with <see cref="PendingUserChange.After"/>.</summary>
    Write,

    /// <summary>Delete the page. <see cref="PendingUserChange.Before"/> is the content being removed; <see cref="PendingUserChange.After"/> is empty.</summary>
    Delete,

    /// <summary>Move the page under <c>/archive/</c> on disk, out of every scope's index (FR-MEM-08).</summary>
    Archive,
}

/// <summary>
/// A staged change awaiting human review in the PWA (steward path, FR-MEM-05; consolidation proposals, FR-MEM-08).
/// <see cref="Before"/> and <see cref="After"/> are the full file contents, so a UI can render the diff.
/// </summary>
public sealed record PendingUserChange(
    string Id,
    string Path,
    string ProposedBy,
    string? Before,
    string After,
    DateTimeOffset At)
{
    public PendingChangeKind Kind { get; init; } = PendingChangeKind.Write;

    /// <summary>Why the change was proposed, for example "duplicate of /agents/ea/a.md".</summary>
    public string? Reason { get; init; }
}
