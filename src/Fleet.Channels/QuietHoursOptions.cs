namespace Fleet.Channels;

/// <summary>Daily local quiet-hour window for phone push routing (FR-CLI-07).</summary>
public sealed record QuietHoursOptions
{
    public bool Enabled { get; init; }

    public TimeOnly StartsAt { get; init; } = new(22, 0);

    public TimeOnly EndsAt { get; init; } = new(7, 0);
}
