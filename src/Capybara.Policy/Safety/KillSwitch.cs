using System.Collections.Concurrent;
using Capybara.Contracts.Agents;

namespace Capybara.Policy.Safety;

/// <summary>
/// Fleet-wide kill switch and per-agent pause (FR-SAF-06). Engaging cancels turns, stops runtimes, freezes the broker
/// and pauses routines via <see cref="Engaged"/> subscribers. Releasing requires the desktop.
/// </summary>
public sealed class KillSwitch(TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private CancellationTokenSource _fleetToken = new();

    public event EventHandler<KillSwitchEventArgs>? Engaged;

    public event EventHandler<KillSwitchEventArgs>? Released;

    public bool IsEngaged { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset? EngagedAt { get; private set; }

    /// <summary>Cancelled when the kill switch is engaged. Link every turn to it.</summary>
    public CancellationToken FleetToken
    {
        get
        {
            lock (_gate)
            {
                return _fleetToken.Token;
            }
        }
    }

    public void Engage(string reason)
    {
        CancellationTokenSource toCancel;
        lock (_gate)
        {
            if (IsEngaged)
            {
                return;
            }

            IsEngaged = true;
            Reason = reason;
            EngagedAt = time.GetUtcNow();
            toCancel = _fleetToken;
        }

        toCancel.Cancel();
        Engaged?.Invoke(this, new KillSwitchEventArgs(reason, EngagedAt.Value));
    }

    /// <summary>Resume the fleet. Only permitted from the desktop surface.</summary>
    public void Release(ControlSurface surface)
    {
        if (surface != ControlSurface.Desktop)
        {
            throw new InvalidOperationException("Resuming after the kill switch requires the desktop.");
        }

        lock (_gate)
        {
            if (!IsEngaged)
            {
                return;
            }

            IsEngaged = false;
            Reason = null;
            EngagedAt = null;
            _fleetToken.Dispose();
            _fleetToken = new CancellationTokenSource();
        }

        Released?.Invoke(this, new KillSwitchEventArgs("released", time.GetUtcNow()));
    }

    public void Pause(AgentId agent) => _paused.TryAdd(agent.Value, 0);

    public void Resume(AgentId agent) => _paused.TryRemove(agent.Value, out _);

    public bool IsBlocked(AgentId agent) => IsEngaged || _paused.ContainsKey(agent.Value);

    public void Dispose()
    {
        lock (_gate)
        {
            _fleetToken.Dispose();
        }
    }
}

public sealed class KillSwitchEventArgs(string reason, DateTimeOffset at) : EventArgs
{
    public string Reason { get; } = reason;

    public DateTimeOffset At { get; } = at;
}

/// <summary>Which client surface issued a control action.</summary>
public enum ControlSurface
{
    Phone,
    Desktop,
    Tui,

    /// <summary>
    /// A webhook relay, not a person: it may only post events (<c>POST /api/events/…</c>) and is refused everywhere else,
    /// including every human action (FR-RTN-07).
    /// </summary>
    EventRelay,
}
