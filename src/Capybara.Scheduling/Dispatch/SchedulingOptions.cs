namespace Capybara.Scheduling.Dispatch;

/// <summary>Scheduling runtime options (FR-RTN-03, FR-RTN-05, FR-RTN-06).</summary>
public sealed record SchedulingOptions
{
    public string SQLitePath { get; init; } = "capybara-scheduling.sqlite";

    public int ConcurrencyCap { get; init; } = 2;

    public CatchUpPolicy CatchUpPolicy { get; init; } = CatchUpPolicy.Skip;

    public int AutoPauseDays { get; init; } = 14;

    public TimeSpan AlertLead { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Explicit catch-up policy after scheduler downtime (FR-RTN-05).</summary>
public enum CatchUpPolicy
{
    FireOnceNow,
    Skip,
}
