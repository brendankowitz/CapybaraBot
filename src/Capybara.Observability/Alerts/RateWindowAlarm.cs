namespace Capybara.Observability.Alerts;

/// <summary>
/// Per-key sliding-window counter that reports at most one breach per window: once a key reaches the threshold it
/// stays quiet until a full window has passed since the last report. Thread-safe.
/// </summary>
internal sealed class RateWindowAlarm(RateAlertOptions options, TimeProvider time)
{
    private readonly Dictionary<string, State> _keys = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Records one event for <paramref name="key"/>; <see langword="true"/> when it should raise an alert now.</summary>
    public bool Record(string key, out int count)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var state))
            {
                state = new State();
                _keys[key] = state;
            }

            state.Events.Enqueue(now);
            while (state.Events.Peek() <= now - options.Window)
            {
                state.Events.Dequeue();
            }

            count = state.Events.Count;
            if (count < options.Threshold || (state.LastAlert is { } last && now - last < options.Window))
            {
                return false;
            }

            state.LastAlert = now;
            return true;
        }
    }

    private sealed class State
    {
        public Queue<DateTimeOffset> Events { get; } = new();

        public DateTimeOffset? LastAlert { get; set; }
    }
}
