using Capybara.Contracts.Agents;

namespace Capybara.Observability.Alerts;

/// <summary>
/// Alerts when an agent's egress denials spike (FR-OBS-05, FR-SAF-07): a burst of blocked outbound connections is a
/// signal of attempted exfiltration. Raises one alert per agent per window; payloads carry no destinations.
/// </summary>
public sealed class EgressSpikeDetector(IAlertSink alerts, TimeProvider time, RateAlertOptions? options = null)
{
    public static RateAlertOptions DefaultOptions { get; } = new(Threshold: 10, Window: TimeSpan.FromMinutes(5));

    private readonly RateWindowAlarm _alarm = new(options ?? DefaultOptions, time);

    public async ValueTask RecordDenialAsync(AgentId agent, CancellationToken cancellationToken = default)
    {
        if (_alarm.Record(agent.Value, out var count))
        {
            await alerts.PublishAsync(
                new Alert("egress.spike", AlertSeverity.Warning, $"{agent} had {count} egress denials in the last window.", time.GetUtcNow(), agent.Value),
                cancellationToken);
        }
    }
}
