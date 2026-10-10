using System.Collections.Concurrent;
using Capybara.Contracts.Messaging;

namespace Capybara.Policy.Provenance;

/// <summary>Tracks which sessions have ingested untrusted content (FR-SAF-03). Taint is sticky for a session's lifetime.</summary>
public sealed class TaintTracker
{
    private readonly ConcurrentDictionary<string, byte> _tainted = new(StringComparer.Ordinal);

    public void MarkTainted(string sessionId) => _tainted.TryAdd(sessionId, 0);

    public void Apply(string sessionId, Taint taint)
    {
        if (taint == Taint.Tainted)
        {
            MarkTainted(sessionId);
        }
    }

    public bool IsTainted(string sessionId) => _tainted.ContainsKey(sessionId);

    public Taint Of(string sessionId) => IsTainted(sessionId) ? Taint.Tainted : Taint.Clean;

    /// <summary>Content flowing from <paramref name="source"/> into <paramref name="target"/> carries its taint.</summary>
    public void Propagate(string source, string target)
    {
        if (IsTainted(source))
        {
            MarkTainted(target);
        }
    }

    public void Forget(string sessionId) => _tainted.TryRemove(sessionId, out _);
}
