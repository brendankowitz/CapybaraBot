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
/// Thread-safe. With a journal, every change of state (a session becoming tainted, or a tainted session being
/// forgotten when it ends) is appended before the call returns; the in-memory state changes first, so a failed write
/// still leaves this process conservative and the exception tells the caller the change is not durable.
/// </remarks>
public sealed class TaintTracker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HashSet<string> _tainted = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string? _journal;

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

    public void MarkTainted(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_tainted.Add(sessionId))
            {
                Persist(new TaintRecord(sessionId, Tainted: true));
            }
        }
    }

    public void Apply(string sessionId, Taint taint)
    {
        if (taint == Taint.Tainted)
        {
            MarkTainted(sessionId);
        }
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

    /// <summary>The session has ended for good (deleted); a later session with the same id starts clean.</summary>
    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            if (_tainted.Remove(sessionId))
            {
                Persist(new TaintRecord(sessionId, Tainted: false));
            }
        }
    }

    private void Persist(TaintRecord record)
    {
        if (_journal is not null)
        {
            JsonlFile.Append(_journal, record, Json);
        }
    }

    /// <summary>One journal line: a session became tainted, or (a tombstone) a tainted session ended.</summary>
    private sealed record TaintRecord(string SessionId, bool Tainted);
}
