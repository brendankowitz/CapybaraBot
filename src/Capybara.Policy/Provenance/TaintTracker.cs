using System.Text.Json;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Storage;

namespace Capybara.Policy.Provenance;

/// <summary>
/// Tracks which sessions have ingested untrusted content (FR-SAF-03). Taint is sticky for a session's lifetime, and
/// sessions outlive the process (main and task sessions resume with their full history), so a journaled tracker
/// replays its state on construction: a restart never turns a tainted session clean.
/// </summary>
/// <remarks>
/// Thread-safe. The in-memory state always changes first, so this process is conservative whatever the disk does. After a
/// failed write (which may have left a torn line), the next change, or the next turn (<see cref="Apply"/> is called for
/// every turn), rewrites the journal atomically from the in-memory state; every failed write raises
/// <see cref="PersistenceFailed"/> so the host can alert. Never throws for a failed write: the taint is already in force.
/// </remarks>
public sealed class TaintTracker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HashSet<string> _tainted = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string? _journal;

    // A write failed, so the journal may lack changes or end in a torn line: the next write replaces it atomically.
    private bool _unsaved;

    /// <summary>An in-memory tracker (tests and processes that own no state).</summary>
    public TaintTracker()
    {
    }

    /// <summary>
    /// A tracker persisted to the append-only JSON-lines <paramref name="journalPath"/>: replayed now, compacted to the
    /// live set, and appended to on every change.
    /// </summary>
    /// <exception cref="InvalidDataException">The journal is corrupt before its last line.</exception>
    public TaintTracker(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        _journal = journalPath;
        var records = JsonlFile.Replay<TaintRecord>(journalPath, Json);
        foreach (var record in records)
        {
            if (record.Tainted)
            {
                _tainted.Add(record.SessionId);
            }
            else
            {
                _tainted.Remove(record.SessionId);
            }
        }

        if (records.Count > _tainted.Count)
        {
            JsonlFile.Rewrite(journalPath, _tainted.Order(StringComparer.Ordinal).Select(id => new TaintRecord(id, Tainted: true)), Json);
        }
    }

    /// <summary>
    /// A change could not be written to the journal (it is retried). Raised outside the tracker's lock; handlers must not
    /// throw.
    /// </summary>
    public event EventHandler<Exception>? PersistenceFailed;

    /// <summary>The journal lacks changes this process made (a write failed and no retry has succeeded yet).</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            lock (_gate)
            {
                return _unsaved;
            }
        }
    }

    public void MarkTainted(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Exception? failure;
        lock (_gate)
        {
            failure = _tainted.Add(sessionId) ? Persist(new TaintRecord(sessionId, Tainted: true)) : Flush();
        }

        Report(failure);
    }

    /// <summary>Applies a turn's incoming taint; on every turn, also retries journal writes that failed before.</summary>
    public void Apply(string sessionId, Taint taint)
    {
        if (taint == Taint.Tainted)
        {
            MarkTainted(sessionId);
            return;
        }

        Exception? failure;
        lock (_gate)
        {
            failure = Flush();
        }

        Report(failure);
    }

    public bool IsTainted(string sessionId)
    {
        lock (_gate)
        {
            return _tainted.Contains(sessionId);
        }
    }

    public Taint Of(string sessionId) => IsTainted(sessionId) ? Taint.Tainted : Taint.Clean;

    /// <summary>Content flowing from <paramref name="source"/> into <paramref name="target"/> carries its taint.</summary>
    public void Propagate(string source, string target)
    {
        if (IsTainted(source))
        {
            MarkTainted(target);
        }
    }

    /// <summary>
    /// The session has ended for good (deleted); a later session with the same id starts clean. A tombstone that cannot be
    /// written only leaves the session tainted on disk, the safe side.
    /// </summary>
    public void Forget(string sessionId)
    {
        Exception? failure;
        lock (_gate)
        {
            failure = _tainted.Remove(sessionId) ? Persist(new TaintRecord(sessionId, Tainted: false)) : Flush();
        }

        Report(failure);
    }

    /// <summary>Appends <paramref name="record"/>, or rewrites the journal if an earlier write failed. Caller holds the lock.</summary>
    private Exception? Persist(TaintRecord record)
    {
        if (_journal is null)
        {
            return null;
        }

        if (_unsaved)
        {
            return Flush();
        }

        try
        {
            JsonlFile.Append(_journal, record, Json);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            return ex;
        }
    }

    /// <summary>After a failed write, replaces the journal with the live set (temp file, then move). Caller holds the lock.</summary>
    private Exception? Flush()
    {
        if (_journal is null || !_unsaved)
        {
            return null;
        }

        try
        {
            JsonlFile.Rewrite(_journal, _tainted.Order(StringComparer.Ordinal).Select(id => new TaintRecord(id, Tainted: true)), Json);
            _unsaved = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }

    private void Report(Exception? failure)
    {
        if (failure is not null)
        {
            PersistenceFailed?.Invoke(this, failure);
        }
    }

    /// <summary>One journal line: a session became tainted, or (a tombstone) a tainted session ended.</summary>
    private sealed record TaintRecord(string SessionId, bool Tainted);
}
