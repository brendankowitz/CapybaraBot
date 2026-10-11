using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Observability.Tests;

public sealed class AlertDetectorTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private static readonly AgentId Code = AgentId.Parse("code");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAlertSink _alerts = new();

    [Fact]
    public async Task Egress_spike_alerts_once_per_window_per_agent()
    {
        var detector = new EgressSpikeDetector(_alerts, _time, new RateAlertOptions(3, TimeSpan.FromMinutes(5)));
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 2; i++)
        {
            await detector.RecordDenialAsync(Ea, ct);
        }

        Assert.Empty(_alerts.Recent);

        for (var i = 0; i < 10; i++)
        {
            await detector.RecordDenialAsync(Ea, ct);
        }

        var first = Assert.Single(_alerts.Recent);
        Assert.Equal("egress.spike", first.Kind);
        Assert.Equal("ea", first.Agent);

        // Still inside the window: no repeat. A different agent is tracked separately.
        _time.Advance(TimeSpan.FromMinutes(4));
        await detector.RecordDenialAsync(Ea, ct);
        Assert.Single(_alerts.Recent);

        for (var i = 0; i < 3; i++)
        {
            await detector.RecordDenialAsync(Code, ct);
        }

        Assert.Equal(2, _alerts.Recent.Count);

        // A full window later the spike is reported again.
        _time.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 3; i++)
        {
            await detector.RecordDenialAsync(Ea, ct);
        }

        Assert.Equal(3, _alerts.Recent.Count);
    }

    [Fact]
    public async Task Slow_denials_below_the_rate_never_alert()
    {
        var detector = new EgressSpikeDetector(_alerts, _time, new RateAlertOptions(3, TimeSpan.FromMinutes(5)));

        for (var i = 0; i < 20; i++)
        {
            await detector.RecordDenialAsync(Ea, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromMinutes(3));
        }

        Assert.Empty(_alerts.Recent);
    }

    [Fact]
    public async Task Compaction_storm_alerts_once_per_window_with_default_thresholds()
    {
        var detector = new CompactionStormDetector(_alerts, _time);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < CompactionStormDetector.DefaultOptions.Threshold * 2; i++)
        {
            await detector.RecordCompactionAsync(Ea, ct);
        }

        var alert = Assert.Single(_alerts.Recent);
        Assert.Equal("compaction.storm", alert.Kind);

        _time.Advance(CompactionStormDetector.DefaultOptions.Window);
        for (var i = 0; i < CompactionStormDetector.DefaultOptions.Threshold; i++)
        {
            await detector.RecordCompactionAsync(Ea, ct);
        }

        Assert.Equal(2, _alerts.Recent.Count);
    }

    [Fact]
    public void Options_reject_non_positive_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateAlertOptions(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateAlertOptions(1, TimeSpan.Zero));
    }

    [Fact]
    public async Task File_alert_sink_replays_recent_alerts_and_tolerates_a_torn_last_line()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "alerts", "alerts.jsonl");
        var ct = TestContext.Current.CancellationToken;
        using (var sink = new FileAlertSink(path, capacity: 2))
        {
            for (var i = 0; i < 3; i++)
            {
                await sink.PublishAsync(new Alert("k", AlertSeverity.Critical, $"m{i}", _time.GetUtcNow(), "ea"), ct);
            }

            Assert.Equal(["m1", "m2"], sink.Recent.Select(a => a.Message));
        }

        await File.AppendAllTextAsync(path, "{\"kind\":\"k\",\"sever", ct);

        using var reopened = new FileAlertSink(path, capacity: 2);
        Assert.Equal(["m1", "m2"], reopened.Recent.Select(a => a.Message));
        Assert.Equal(AlertSeverity.Critical, reopened.Recent[0].Severity);

        await reopened.PublishAsync(new Alert("k", AlertSeverity.Info, "m3", _time.GetUtcNow()), ct);
        using var third = new FileAlertSink(path, capacity: 10);
        Assert.Equal(["m0", "m1", "m2", "m3"], third.Recent.Select(a => a.Message));
    }
}
