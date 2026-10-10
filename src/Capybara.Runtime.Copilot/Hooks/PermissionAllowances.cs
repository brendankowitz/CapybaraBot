using System.Collections.Concurrent;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// Bridges the two SDK enforcement points. A permission request is only approved if the policy gate allowed the
/// tool call that caused it in <c>OnPreToolUse</c>; anything else is rejected (fail closed, FR-AGT-05).
/// </summary>
public sealed class PermissionAllowances
{
    private readonly ConcurrentDictionary<string, int> _pending = new(StringComparer.Ordinal);

    public void Grant(string sessionId) => _pending.AddOrUpdate(sessionId, 1, (_, n) => n + 1);

    public bool TryConsume(string sessionId)
    {
        while (_pending.TryGetValue(sessionId, out var count))
        {
            if (count <= 0)
            {
                return false;
            }

            if (_pending.TryUpdate(sessionId, count - 1, count))
            {
                return true;
            }
        }

        return false;
    }

    public void Clear(string sessionId) => _pending.TryRemove(sessionId, out _);
}
