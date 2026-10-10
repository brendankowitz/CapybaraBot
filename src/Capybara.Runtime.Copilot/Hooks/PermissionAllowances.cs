using System.Collections.Concurrent;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// Bridges the SDK enforcement points to the policy gate's decision in <c>OnPreToolUse</c> (fail closed, FR-AGT-05).
/// A permission request is only approved if the gate allowed the tool call that caused it, and a host-invoked function
/// that is wrapped by <see cref="SessionHandlers.GateInvocation"/> (broker tools) only runs if the gate allowed a call
/// to that tool. Anything else is rejected.
/// </summary>
public sealed class PermissionAllowances
{
    private readonly ConcurrentDictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Session, string Tool), int> _invocations = new();

    public void Grant(string sessionId) => _pending.AddOrUpdate(sessionId, 1, (_, n) => n + 1);

    public bool TryConsume(string sessionId) => TryDecrement(_pending, sessionId);

    /// <summary>The gate allowed one call of <paramref name="tool"/> in <paramref name="sessionId"/>.</summary>
    public void GrantInvocation(string sessionId, string tool) => _invocations.AddOrUpdate((sessionId, tool), 1, (_, n) => n + 1);

    public bool TryConsumeInvocation(string sessionId, string tool) => TryDecrement(_invocations, (sessionId, tool));

    public void Clear(string sessionId)
    {
        _pending.TryRemove(sessionId, out _);
        foreach (var key in _invocations.Keys.Where(k => k.Session == sessionId))
        {
            _invocations.TryRemove(key, out _);
        }
    }

    private static bool TryDecrement<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        while (counts.TryGetValue(key, out var count))
        {
            if (count <= 0)
            {
                return false;
            }

            if (counts.TryUpdate(key, count - 1, count))
            {
                return true;
            }
        }

        return false;
    }
}
