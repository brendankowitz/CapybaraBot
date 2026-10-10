using Capybara.Contracts.Agents;

namespace Capybara.Observability.Alerts;

/// <summary>
/// Alerts when an agent compacts its context repeatedly in a short time (FR-OBS-05): a compaction storm means the
/// session is thrashing and burning credits. Raises one alert per agent per window.
/// </summary>
public sealed class CompactionStormDetector(IAlertSink alerts, TimeProvider time, RateAlertOptions? options = null)
{
    public static RateAlertOptions DefaultOptions { get; } = new(Threshold: 3, Window: TimeSpan.FromMinutes(10));

    private readonly RateWindowAlarm _alarm = new(options ?? DefaultOptions, time);

    public async ValueTask RecordCompactionAsync(AgentId agent, CancellationToken cancellationToken = default)
    {
        if (_alarm.Record(agent.Value, out var count))
        {
            await alerts.PublishAsync(
                new Alert("compaction.storm", AlertSeverity.Warning, $"{agent} compacted its context {count} times in the last window.", time.GetUtcNow(), agent.Value),
                cancellationToken);
        }
    }
}
