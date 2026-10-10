namespace Capybara.Observability.Alerts;

/// <summary>Threshold for a rate-based detector: <see cref="Threshold"/> events within <see cref="Window"/>.</summary>
public sealed record RateAlertOptions(int Threshold, TimeSpan Window)
{
    public int Threshold { get; } = Threshold > 0 ? Threshold : throw new ArgumentOutOfRangeException(nameof(Threshold));

    public TimeSpan Window { get; } = Window > TimeSpan.Zero ? Window : throw new ArgumentOutOfRangeException(nameof(Window));
}
