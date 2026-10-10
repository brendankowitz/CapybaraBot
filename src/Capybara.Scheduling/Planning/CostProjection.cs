using Capybara.Contracts.Routines;

namespace Capybara.Scheduling.Planning;

/// <summary>
/// Projects monthly credit burn before a routine is enabled (FR-RTN-09). Every heartbeat is a billed model loop.
/// </summary>
public static class CostProjection
{
    private const double DaysPerMonth = 30.0;

    /// <summary>
    /// Projected billed runs in a month, evaluating cron expressions in <paramref name="timeZone"/> (UTC when omitted).
    /// Cron expressions use the worst 30-day window of a year (see <see cref="CronSchedule.FiresPerMonth"/>).
    /// </summary>
    public static double RunsPerMonth(RoutineSpec routine, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.Kind == TriggerKind.Event)
        {
            return 0;
        }

        // A cron routine's expression takes precedence over Interval (RoutineSpec.Cron). The cron itself defines when it
        // fires, so active hours do not apply.
        if (routine.Kind == TriggerKind.Cron && !string.IsNullOrWhiteSpace(routine.Cron))
        {
            return CronSchedule.TryParse(routine.Cron, timeZone ?? TimeZoneInfo.Utc, out var cron, out _)
                ? CronSchedule.FiresPerMonth(cron)
                : 0;
        }

        if (routine.Interval is not { } interval || interval <= TimeSpan.Zero)
        {
            return 0;
        }

        var activeHoursPerDay = 24.0;
        if (routine.ActiveFrom is { } from && routine.ActiveTo is { } to)
        {
            var span = to - from;
            activeHoursPerDay = (span <= TimeSpan.Zero ? span + TimeSpan.FromDays(1) : span).TotalHours;
        }

        // Rarer than once per active day (a weekly routine): active hours cannot make it cheaper, and it must not round down to zero.
        return interval.TotalHours >= activeHoursPerDay
            ? Math.Max(1, Math.Ceiling(DaysPerMonth * 24.0 / interval.TotalHours))
            : Math.Floor(activeHoursPerDay / interval.TotalHours) * DaysPerMonth;
    }

    public static decimal MonthlyCredits(RoutineSpec routine, TimeZoneInfo? timeZone = null) =>
        (decimal)RunsPerMonth(routine, timeZone) * routine.BudgetCreditsPerRun;
}
