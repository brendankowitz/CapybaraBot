using Capybara.Observability.Alerts;

namespace Capybara.Observability.Cost;

/// <summary>
/// Polls the account quota and alerts when usage first crosses 50%, 80% and 100% (FR-OBS-02, NFR-03). Each threshold
/// alerts once per billing period; a poll that crosses several at once sends only the highest, to avoid noise.
/// State is in memory, so a restart can repeat at most the highest already-crossed alert.
/// </summary>
public sealed class QuotaMonitor(IQuotaSource source, IAlertSink alerts, TimeProvider time)
{
    private static readonly decimal[] Thresholds = [0.5m, 0.8m, 1.0m];

    private DateTimeOffset? _period;
    private bool _periodKnown;
    private decimal _highestAlerted;
    private bool _failing;

    /// <summary>The most recent successful reading, or <see langword="null"/> before the first one.</summary>
    public QuotaSnapshot? Latest { get; private set; }

    /// <summary>Reads the quota once and raises any newly crossed threshold alert.</summary>
    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await source.GetQuotaAsync(cancellationToken);
        Latest = snapshot;
        _failing = false;

        if (!_periodKnown || snapshot.ResetsAt != _period)
        {
            _period = snapshot.ResetsAt;
            _periodKnown = true;
            _highestAlerted = 0;
        }

        var fraction = snapshot.Fraction;
        var crossed = Thresholds.Where(t => fraction >= t && t > _highestAlerted).ToList();
        if (crossed.Count == 0)
        {
            return;
        }

        var threshold = crossed[^1];
        _highestAlerted = threshold;
        await alerts.PublishAsync(
            new Alert(
                "quota.threshold",
                threshold >= 1.0m ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"Copilot plan quota reached {threshold * 100:0}% ({snapshot.Used:0.##} of {snapshot.Limit:0.##} credits).",
                time.GetUtcNow()),
            cancellationToken);
    }

    /// <summary>
    /// Polls every <paramref name="interval"/> until cancelled. A failing source raises one warning alert per failure
    /// streak and polling continues.
    /// </summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await PollAsync(cancellationToken);
            }
#pragma warning disable CA1031 // A flaky quota endpoint must not stop monitoring.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                if (!_failing)
                {
                    _failing = true;
                    await alerts.PublishAsync(
                        new Alert("quota.poll_failed", AlertSeverity.Warning, $"Quota poll failed: {ex.GetType().Name}.", time.GetUtcNow()),
                        cancellationToken);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
