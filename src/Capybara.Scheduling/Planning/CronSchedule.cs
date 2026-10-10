using Capybara.Scheduling.Validation;
using Quartz;

namespace Capybara.Scheduling.Planning;

/// <summary>
/// Quartz cron expression helpers shared by validation (minimum interval), cost projection and scheduling
/// (FR-RTN-03, FR-RTN-09). Expressions use Quartz syntax: seconds first, <c>?</c> for day-of-month or day-of-week.
/// </summary>
public static class CronSchedule
{
    /// <summary>Fixed anchor so projections and validation are deterministic, not dependent on today's date.</summary>
    private static readonly DateTimeOffset SampleAnchor = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SampleWindow = TimeSpan.FromDays(366);
    private static readonly TimeSpan ProjectionWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// More fires than fit into the sample window at the minimum interval means some gap is too short, so sampling can stop.
    /// Doubles as the memory bound when projecting an (invalid) very frequent expression.
    /// </summary>
    private static readonly int MaxSamples = (int)(SampleWindow / RoutineLimits.MinimumInterval) + 1;

    /// <summary>Parses <paramref name="expression"/>; returns false with a human-readable reason when invalid.</summary>
    public static bool TryParse(string? expression, TimeZoneInfo timeZone, out CronExpression cron, out string? error)
    {
        try
        {
            cron = CronExpression.Parse(expression ?? string.Empty).WithTimeZone(timeZone);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            cron = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The next fire time strictly after <paramref name="after"/>, or null if the expression never fires again.</summary>
    public static DateTimeOffset? NextFire(CronExpression cron, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(cron);
        return cron.GetNextValidTimeAfter(after);
    }

    /// <summary>
    /// The shortest gap below <see cref="RoutineLimits.MinimumInterval"/> found between consecutive fires, or null when
    /// every gap is long enough (FR-RTN-03). Samples Quartz's own next-fire computation over 366 days starting at the
    /// expression's first fire, so year-pinned expressions are checked too.
    /// </summary>
    public static TimeSpan? TooFrequentGap(CronExpression cron)
    {
        ArgumentNullException.ThrowIfNull(cron);
        DateTimeOffset? previous = null;
        foreach (var current in Fires(cron))
        {
            if (previous is { } p && current - p < RoutineLimits.MinimumInterval)
            {
                return current - p;
            }

            previous = current;
        }

        return null;
    }

    /// <summary>
    /// Fires in the worst 30-day window found within a year of sampling — the monthly run count used for cost projection
    /// (FR-RTN-09). Using the worst window rather than a year's average means seasonal expressions (<c>JUN-AUG</c>) are not
    /// under-projected. An expression that fires at all projects at least 1; one that never fires projects 0.
    /// </summary>
    public static int FiresPerMonth(CronExpression cron)
    {
        ArgumentNullException.ThrowIfNull(cron);
        var fires = Fires(cron).ToList();
        var worst = 0;
        var end = 0;
        for (var start = 0; start < fires.Count; start++)
        {
            while (end < fires.Count && fires[end] - fires[start] < ProjectionWindow)
            {
                end++;
            }

            worst = Math.Max(worst, end - start);
        }

        return worst;
    }

    /// <summary>The fires in the sample window, lazily, stopping at <see cref="MaxSamples"/>.</summary>
    private static IEnumerable<DateTimeOffset> Fires(CronExpression cron)
    {
        var next = cron.GetNextValidTimeAfter(SampleAnchor.AddSeconds(-1));
        if (next is not { } first)
        {
            yield break;
        }

        var end = first + SampleWindow;
        for (var i = 0; i < MaxSamples && next is { } current && current < end; i++)
        {
            yield return current;
            next = cron.GetNextValidTimeAfter(current);
        }
    }
}
