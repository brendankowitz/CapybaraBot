namespace Capybara.Scheduling.Dispatch;

/// <summary>Scheduling runtime options (FR-RTN-03, FR-RTN-05, FR-RTN-06).</summary>
public sealed record SchedulingOptions
{
    /// <summary>SQLite file holding the Quartz job store, the durable run history and auto-pause interaction times (FR-RTN-05).</summary>
    public string SQLitePath { get; init; } = "capybara-scheduling.sqlite";

    /// <summary>Fleet-wide cap on routines executing at once (FR-RTN-03).</summary>
    public int ConcurrencyCap { get; init; } = 2;

    /// <summary>
    /// What happens to a run whose scheduled time passed while the scheduler was down or paused (FR-RTN-05).
    /// Either way the missed run is recorded and alerted; only <see cref="CatchUpPolicy.FireOnceNow"/> then executes it.
    /// Several missed fires of one routine collapse into a single catch-up — never a burst.
    /// </summary>
    public CatchUpPolicy CatchUpPolicy { get; init; } = CatchUpPolicy.Skip;

    /// <summary>How late a firing may be before it counts as missed rather than merely delayed by scheduler latency.</summary>
    public TimeSpan MisfireTolerance { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Days without user interaction before an unattended routine auto-pauses (FR-RTN-06).</summary>
    public int AutoPauseDays { get; init; } = 14;

    /// <summary>How often the auto-pause check runs (FR-RTN-06).</summary>
    public TimeSpan AutoPauseCheckInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan AlertLead { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the scheduler checks that every runnable routine still has a live trigger and re-plans any that lost theirs (FR-RTN-05).</summary>
    public TimeSpan RepairInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>First wait before re-planning a routine's next fire after a scheduler failure; it doubles per attempt (four attempts).</summary>
    public TimeSpan RescheduleRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Time zone for cron expressions and heartbeat active hours. Defaults to the host's local zone because a user's
    /// "07:00–21:00" and "every day at 08:00" are wall-clock statements.
    /// </summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
}

/// <summary>Explicit catch-up policy after scheduler downtime (FR-RTN-05).</summary>
public enum CatchUpPolicy
{
    /// <summary>Record and alert the missed run, then execute exactly one catch-up run now.</summary>
    FireOnceNow,

    /// <summary>Record and alert the missed run, and wait for the next regular fire. Nothing is executed.</summary>
    Skip,
}
