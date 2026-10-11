namespace Capybara.Hosting.Backup;

/// <summary>Daily backup schedule at a fixed UTC time of day (FR-HST-05 "nightly").</summary>
public sealed record BackupScheduleOptions(TimeOnly UtcTimeOfDay)
{
    /// <summary>Nightly at 02:00 UTC.</summary>
    public static BackupScheduleOptions Default { get; } = new(new TimeOnly(2, 0));

    /// <summary>The first scheduled run strictly after <paramref name="now"/>.</summary>
    public DateTimeOffset NextRunAfter(DateTimeOffset now)
    {
        var today = SlotOn(now);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>The most recent scheduled run at or before <paramref name="now"/>; a backup older than this is overdue.</summary>
    public DateTimeOffset LastRunAtOrBefore(DateTimeOffset now) => NextRunAfter(now).AddDays(-1);

    private DateTimeOffset SlotOn(DateTimeOffset now) =>
        new(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(UtcTimeOfDay), TimeSpan.Zero);
}
