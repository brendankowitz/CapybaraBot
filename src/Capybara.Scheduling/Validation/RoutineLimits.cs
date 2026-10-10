namespace Capybara.Scheduling.Validation;

/// <summary>Routine limits (FR-RTN-03).</summary>
public static class RoutineLimits
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest accepted interval. Far beyond any sensible routine, but well inside what <see cref="DateTimeOffset"/>
    /// arithmetic can add to "now", so a routine can never overflow the scheduler.
    /// </summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(366);

    public const int MaxRoutinesPerAgent = 50;

    public const int RunHistoryDepth = 20;

    public const double JitterFraction = 0.10;
}
