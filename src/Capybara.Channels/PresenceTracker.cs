using System.Collections.Concurrent;

namespace Capybara.Channels;

/// <summary>Focus heartbeat tracker with expiring device state for notification suppression (FR-CLI-07).</summary>
public sealed class PresenceTracker(TimeProvider timeProvider, TimeSpan focusTtl) : IPresenceTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _focusedUntil = new(StringComparer.Ordinal);

    public void SetFocused(string deviceId, bool focused)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!focused)
        {
            _focusedUntil.TryRemove(deviceId, out _);
            return;
        }

        _focusedUntil[deviceId] = timeProvider.GetUtcNow() + focusTtl;
    }

    public bool IsAnyFocused()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _focusedUntil)
        {
            if (pair.Value > now)
            {
                return true;
            }

            _focusedUntil.TryRemove(pair.Key, out _);
        }

        return false;
    }
}
