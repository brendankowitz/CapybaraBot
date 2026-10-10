using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Observability.Tests;

public sealed class QuotaMonitorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAlertSink _alerts = new();
    private readonly FakeQuotaSource _source = new();

    [Fact]
    public async Task Alerts_at_50_80_and_100_percent_exactly_once()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        var ct = TestContext.Current.CancellationToken;
        var resets = _time.GetUtcNow().AddDays(20);

        foreach (var used in new[] { 100m, 499m, 500m, 600m, 800m, 801m, 950m, 1000m, 1000m, 1200m })
        {
            _source.Next = new QuotaSnapshot(used, 1000m, resets);
            await monitor.PollAsync(ct);
        }

        var alerts = _alerts.Recent.Where(a => a.Kind == "quota.threshold").ToList();
        Assert.Equal(3, alerts.Count);
        Assert.Contains("50%", alerts[0].Message, StringComparison.Ordinal);
        Assert.Contains("80%", alerts[1].Message, StringComparison.Ordinal);
        Assert.Contains("100%", alerts[2].Message, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.Critical, alerts[2].Severity);
    }

    [Fact]
    public async Task A_jump_across_several_thresholds_sends_only_the_highest()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        _source.Next = new QuotaSnapshot(850m, 1000m);

        await monitor.PollAsync(TestContext.Current.CancellationToken);

        var alert = Assert.Single(_alerts.Recent);
        Assert.Contains("80%", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_billing_period_re_arms_the_alerts()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        var ct = TestContext.Current.CancellationToken;
        _source.Next = new QuotaSnapshot(600m, 1000m, _time.GetUtcNow().AddDays(5));
        await monitor.PollAsync(ct);

        _source.Next = new QuotaSnapshot(100m, 1000m, _time.GetUtcNow().AddDays(35));
        await monitor.PollAsync(ct);
        _source.Next = new QuotaSnapshot(550m, 1000m, _time.GetUtcNow().AddDays(35));
        await monitor.PollAsync(ct);

        Assert.Equal(2, _alerts.Recent.Count(a => a.Kind == "quota.threshold"));
        Assert.Equal(550m, monitor.Latest?.Used);
    }

    [Fact]
    public async Task RunAsync_polls_on_the_interval_and_reports_a_failure_streak_once()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _source.Fail = true;
        var run = monitor.RunAsync(TimeSpan.FromMinutes(1), cts.Token);

        await WaitForAsync(() => _source.Calls >= 1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => _source.Calls >= 2);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => _source.Calls >= 3);
        Assert.Single(_alerts.Recent, a => a.Kind == "quota.poll_failed");

        _source.Fail = false;
        _source.Next = new QuotaSnapshot(900m, 1000m);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => _alerts.Recent.Any(a => a.Kind == "quota.threshold"));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_timeout_surfacing_as_TaskCanceledException_does_not_stop_the_monitor()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _source.FailWith = new TaskCanceledException("http timeout");
        var run = monitor.RunAsync(TimeSpan.FromMinutes(1), cts.Token);

        await WaitForAsync(() => _source.Calls >= 1);
        await WaitForAsync(() => _alerts.Recent.Any(a => a.Kind == "quota.poll_failed"));
        _source.FailWith = null;
        _source.Next = new QuotaSnapshot(550m, 1000m);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => _alerts.Recent.Any(a => a.Kind == "quota.threshold"));
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_failing_sink_while_reporting_a_failure_does_not_stop_the_monitor()
    {
        var sink = new FlakySink();
        var monitor = new QuotaMonitor(_source, sink, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _source.FailWith = new InvalidOperationException("boom");
        sink.Fail = true;
        var run = monitor.RunAsync(TimeSpan.FromMinutes(1), cts.Token);

        await WaitForAsync(() => sink.Attempts >= 1);
        sink.Fail = false;
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => sink.Published.Any(a => a.Kind == "quota.poll_failed"));
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_new_period_without_a_reset_timestamp_re_arms_when_usage_drops()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        var ct = TestContext.Current.CancellationToken;

        foreach (var used in new[] { 600m, 700m, 50m, 520m })
        {
            _source.Next = new QuotaSnapshot(used, 1000m);
            await monitor.PollAsync(ct);
        }

        Assert.Equal(2, _alerts.Recent.Count(a => a.Kind == "quota.threshold"));
    }

    [Fact]
    public async Task A_jittering_reset_timestamp_does_not_re_fire_alerts()
    {
        var monitor = new QuotaMonitor(_source, _alerts, _time);
        var ct = TestContext.Current.CancellationToken;
        var resets = _time.GetUtcNow().AddDays(20);

        for (var i = 0; i < 6; i++)
        {
            _source.Next = new QuotaSnapshot(600m + i, 1000m, resets.AddSeconds(i % 2 == 0 ? i : -i).AddHours(i));
            await monitor.PollAsync(ct);
        }

        Assert.Single(_alerts.Recent, a => a.Kind == "quota.threshold");
    }

    [Fact]
    public async Task A_sink_failure_leaves_the_threshold_unmarked_so_the_next_poll_retries()
    {
        var sink = new FlakySink { Fail = true };
        var monitor = new QuotaMonitor(_source, sink, _time);
        var ct = TestContext.Current.CancellationToken;
        _source.Next = new QuotaSnapshot(600m, 1000m);

        await Assert.ThrowsAsync<IOException>(() => monitor.PollAsync(ct));
        sink.Fail = false;
        await monitor.PollAsync(ct);
        await monitor.PollAsync(ct);

        Assert.Single(sink.Published, a => a.Kind == "quota.threshold");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the monitor");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FlakySink : IAlertSink
    {
        private readonly List<Alert> _published = [];
        private int _attempts;

        public bool Fail { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public IReadOnlyList<Alert> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        public ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            if (Fail)
            {
                throw new IOException("sink down");
            }

            lock (_published)
            {
                _published.Add(alert);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeQuotaSource : IQuotaSource
    {
        private int _calls;

        public QuotaSnapshot Next { get; set; } = new(0m, 1000m);

        public bool Fail { get; set; }

        public Exception? FailWith { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<QuotaSnapshot> GetQuotaAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (FailWith is { } failure)
            {
                throw failure;
            }

            return Fail ? throw new InvalidOperationException("boom") : Task.FromResult(Next);
        }
    }
}
