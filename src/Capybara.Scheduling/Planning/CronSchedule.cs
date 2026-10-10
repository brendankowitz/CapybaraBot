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

    private const int ValidationWindowDays = 366;
    private const int ProjectionWindowDays = 30;
    private const int MaxValidationSamples = 50_000;
    private const int MaxProjectionSamples = 100_000;

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
    /// Smallest gap between consecutive fires, found by sampling Quartz's own next-fire computation over a year.
    /// Null when the expression fires fewer than twice in that window (nothing can be too frequent).
    /// </summary>
    public static TimeSpan? MinimumGap(CronExpression cron)
    {
        ArgumentNullException.ThrowIfNull(cron);
        var end = SampleAnchor.AddDays(ValidationWindowDays);
        TimeSpan? min = null;
        DateTimeOffset? previous = null;
        var next = cron.GetNextValidTimeAfter(SampleAnchor.AddSeconds(-1));
        for (var i = 0; i < MaxValidationSamples && next is { } current && current < end; i++)
        {
            if (previous is { } p && (min is null || current - p < min))
            {
                min = current - p;
            }

            previous = current;
            next = cron.GetNextValidTimeAfter(current);
        }

        return min;
    }

    /// <summary>Number of fires in a fixed 30-day sample window — the monthly run count used for cost projection (FR-RTN-09).</summary>
    public static int FiresPerMonth(CronExpression cron)
    {
        ArgumentNullException.ThrowIfNull(cron);
        var end = SampleAnchor.AddDays(ProjectionWindowDays);
        var count = 0;
        var next = cron.GetNextValidTimeAfter(SampleAnchor.AddSeconds(-1));
        // The cap keeps projecting a too-frequent (and therefore invalid) expression cheap; real routines fire at most ~8,640 times.
        while (count < MaxProjectionSamples && next is { } current && current < end)
        {
            count++;
            next = cron.GetNextValidTimeAfter(current);
        }

        return count;
    }
}
