namespace Fleet.Runtime.Copilot.Supervision;

public enum RuntimeHealthState
{
    Healthy,
    BackingOff,
    Quarantined,
}

/// <summary>Supervisor view of one agent runtime, surfaced on the dashboard.</summary>
public sealed record RuntimeHealth(string Agent, RuntimeHealthState State, int RecentCrashes, DateTimeOffset? NextAttemptAt);
