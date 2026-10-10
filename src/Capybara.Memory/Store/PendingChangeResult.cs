namespace Capybara.Memory.Store;

/// <summary>Result of resolving a staged change.</summary>
public enum PendingChangeResult
{
    /// <summary>The change was written to the wiki and removed from the queue.</summary>
    Applied,

    /// <summary>A human rejected the change; it was removed without touching the wiki.</summary>
    Discarded,

    /// <summary>The page changed after the proposal was made; the proposal was discarded rather than clobbering newer content.</summary>
    Stale,

    /// <summary>No such change (unknown or invalid id).</summary>
    NotFound,
}
