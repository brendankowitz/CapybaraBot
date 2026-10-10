using Capybara.Contracts.Routines;

namespace Capybara.Scheduling.Planning;

/// <summary>
/// Projects monthly credit burn before a routine is enabled (FR-RTN-09). Every heartbeat is a billed model loop.
/// </summary>
public static class CostProjection
{
    private const double DaysPerMonth = 30.0;

    public static double RunsPerMonth(RoutineSpec routine)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.Kind == TriggerKind.Event)
        {
            return 0;
        }

        // A cron expression takes precedence over Interval (RoutineSpec.Cron). The cron itself defines when it fires, so
        // active hours do not apply; the projection is the number of fires Quartz computes in a fixed 30-day window.
        if (!string.IsNullOrWhiteSpace(routine.Cron))
        {
            return CronSchedule.TryParse(routine.Cron, TimeZoneInfo.Utc, out var cron, out _)
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

        return Math.Floor(activeHoursPerDay / interval.TotalHours) * DaysPerMonth;
    }

    public static decimal MonthlyCredits(RoutineSpec routine) =>
        (decimal)RunsPerMonth(routine) * routine.BudgetCreditsPerRun;
}
