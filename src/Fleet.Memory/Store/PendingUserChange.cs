namespace Fleet.Memory.Store;

/// <summary>A staged <c>/user/</c> edit awaiting review in the PWA (steward path, FR-MEM-05).</summary>
public sealed record PendingUserChange(
    string Id,
    string Path,
    string ProposedBy,
    string? Before,
    string After,
    DateTimeOffset At);
