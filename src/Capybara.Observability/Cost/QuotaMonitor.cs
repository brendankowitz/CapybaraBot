using Capybara.Observability.Alerts;

namespace Capybara.Observability.Cost;

/// <summary>
/// Polls the account quota and alerts when usage first crosses 50%, 80% and 100% (FR-OBS-02, NFR-03). Each threshold
/// alerts once per billing period; a poll that crosses several at once sends only the highest, to avoid noise.
/// A new period is detected when usage drops below the last alerted threshold, or when <see cref="QuotaSnapshot.ResetsAt"/>
/// moves forward by more than <see cref="ResetJitterTolerance"/> (smaller moves are timestamp jitter). A threshold is
/// only marked as alerted after its alert was published, so a failing sink is retried on the next poll.
/// State is in memory, so a restart can repeat at most the highest already-crossed alert.
/// </summary>
public sealed class QuotaMonitor(IQuotaSource source, IAlertSink alerts, TimeProvider time)
{
    /// <summary>Forward movement of <c>ResetsAt</c> up to this size is treated as jitter, not a new period.</summary>
    public static readonly TimeSpan ResetJitterTolerance = TimeSpan.FromDays(1);

    private static readonly decimal[] Thresholds = [0.5m, 0.8m, 1.0m];

    private DateTimeOffset? _resetsAt;
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

        var fraction = snapshot.Fraction;
        var advancedPeriod = snapshot.ResetsAt is { } resetsAt && _resetsAt is { } known && resetsAt - known > ResetJitterTolerance;
        if (advancedPeriod || fraction < _highestAlerted)
        {
            _highestAlerted = 0;
        }

        _resetsAt = snapshot.ResetsAt ?? _resetsAt;

        var threshold = Thresholds.LastOrDefault(t => fraction >= t && t > _highestAlerted);
        if (threshold == 0)
        {
            return;
        }

        await alerts.PublishAsync(
            new Alert(
                "quota.threshold",
                threshold >= 1.0m ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"Copilot plan quota reached {threshold * 100:0}% ({snapshot.Used:0.##} of {snapshot.Limit:0.##} credits).",
                time.GetUtcNow()),
            cancellationToken);
        _highestAlerted = threshold;
    }

    /// <summary>
    /// Polls every <paramref name="interval"/> until cancelled. A failing source (including a timeout surfacing as
    /// <see cref="TaskCanceledException"/>) raises one warning alert per failure streak and polling continues; only
    /// cancellation of <paramref name="cancellationToken"/> ends the loop.
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
#pragma warning disable CA1031 // A flaky quota endpoint or alert sink must not stop monitoring.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await ReportFailureAsync(ex, cancellationToken);
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ReportFailureAsync(Exception failure, CancellationToken cancellationToken)
    {
        if (_failing)
        {
            return;
        }

        try
        {
            await alerts.PublishAsync(
                new Alert("quota.poll_failed", AlertSeverity.Warning, $"Quota poll failed: {failure.GetType().Name}.", time.GetUtcNow()),
                cancellationToken);
            _failing = true;
        }
#pragma warning disable CA1031 // The sink is down as well; keep polling and retry the failure alert on the next failure.
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
        }
    }
}
