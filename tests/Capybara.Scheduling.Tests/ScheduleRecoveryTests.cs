using System.Reflection;
using Capybara.Scheduling.Hosting;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;

namespace Capybara.Scheduling.Tests;

/// <summary>
/// A routine must never be left without a trigger and without an alert (FR-RTN-05): crash recovery, failed re-plans,
/// periodic repair and fail-closed planning.
/// </summary>
public sealed class ScheduleRecoveryTests
{
    private static readonly JobKey HbJob = new("hb", "ea");

    private static SchedulingOptions Options(string path) =>
        new() { SQLitePath = path, TimeZone = TimeZoneInfo.Utc, RescheduleRetryDelay = TimeSpan.FromMilliseconds(10) };

    private static async Task<IReadOnlyList<ITrigger>> TriggersAsync(QuartzHarness harness) =>
        [.. await harness.Scheduler.GetTriggersOfJob(HbJob, TestContext.Current.CancellationToken)];

    [Fact]
    public async Task A_crash_between_fire_and_replan_is_repaired_by_the_hosted_service_start_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var hung = new ScriptedExecutor(blocking: true);
        await using var first = await QuartzHarness.StartAsync(Options, hung);
        first.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await new SchedulerHostedService(first.Scheduler, first.Synchronizer).StartAsync(ct);
        await first.FireChainNowAsync();
        await hung.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The fired one-shot trigger is no longer live (Complete/Executing) and the job never got to plan its successor: this is the state a crash leaves behind.
        var stale = Assert.Single(await TriggersAsync(first));
        Assert.True(await first.Scheduler.GetTriggerState(stale.Key, ct) is TriggerState.Complete or TriggerState.Executing);
        await first.Scheduler.Shutdown(waitForJobsToComplete: false, ct);

        await using var second = await QuartzHarness.StartAsync(Options, existingDatabase: first.DatabasePath);
        second.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        Assert.Equal(stale.Key, Assert.Single(await TriggersAsync(second)).Key);
        Assert.NotEqual(TriggerState.Normal, await second.Scheduler.GetTriggerState(stale.Key, ct));

        await new SchedulerHostedService(second.Scheduler, second.Synchronizer).StartAsync(ct);

        var live = Assert.Single(await TriggersAsync(second));
        Assert.NotEqual(stale.Key, live.Key);
        Assert.Equal(TriggerState.Normal, await second.Scheduler.GetTriggerState(live.Key, ct));
        Assert.True(live.StartTimeUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Repair_replans_a_routine_that_lost_its_trigger_and_the_timer_does_it_unattended()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = Options("unused") with { RepairInterval = TimeSpan.FromMinutes(5) };
        await using var harness = await QuartzHarness.StartAsync(Options, time: time);
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(ct);
        await harness.Scheduler.UnscheduleJobs([.. (await TriggersAsync(harness)).Select(t => t.Key)], ct);
        Assert.Empty(await TriggersAsync(harness));

        using var timer = new ScheduleRepairHostedService(harness.Synchronizer, options, harness.Inner.Alerts, time);
        await timer.StartAsync(ct);
        for (var i = 0; i < 300 && (await TriggersAsync(harness)).Count == 0; i++)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(10, ct);
        }

        await timer.StopAsync(ct);
        Assert.Single(await TriggersAsync(harness));
    }

    [Fact]
    public async Task Repair_leaves_a_running_routine_alone_and_the_job_plans_its_own_next_fire()
    {
        var ct = TestContext.Current.CancellationToken;
        var slow = new ScriptedExecutor(blocking: true);
        await using var harness = await QuartzHarness.StartAsync(Options, slow);
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await new SchedulerHostedService(harness.Scheduler, harness.Synchronizer).StartAsync(ct);
        await harness.FireChainNowAsync();
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var running = Assert.Single(await TriggersAsync(harness)).Key;

        await harness.Synchronizer.RepairAsync(ct);

        Assert.Equal(running, Assert.Single(await TriggersAsync(harness)).Key);
        slow.Release.SetResult();
        await Kit.EventuallyAsync(async () => await TriggersAsync(harness) is [var next] && next.Key != running, "job planned its next fire when it finished");
    }

    [Fact]
    public async Task A_failed_replan_is_retried_with_backoff_and_then_succeeds_without_an_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new TempDirectory();
        using var flaky = await FlakyHarness.StartAsync(directory, failures: 0);
        flaky.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await flaky.Synchronizer.SynchronizeAsync(ct);
        await flaky.Real.UnscheduleJobs([.. (await flaky.Real.GetTriggersOfJob(HbJob, ct)).Select(t => t.Key)], ct);
        var callsBefore = flaky.Proxy.ScheduleJobCalls;

        flaky.Proxy.FailScheduleJob = 2;
        await flaky.Synchronizer.AdvanceAsync("hb", new TriggerKey("fired"), ct);

        Assert.Equal(3, flaky.Proxy.ScheduleJobCalls - callsBefore);
        Assert.Single(await flaky.Real.GetTriggersOfJob(HbJob, ct));
        Assert.Empty(flaky.Alerts.Recent);
    }

    [Fact]
    public async Task A_replan_that_keeps_failing_alerts_once_after_four_attempts_and_repair_recovers_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var directory = new TempDirectory();
        using var flaky = await FlakyHarness.StartAsync(directory, failures: 0);
        flaky.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await flaky.Synchronizer.SynchronizeAsync(ct);
        await flaky.Real.UnscheduleJobs([.. (await flaky.Real.GetTriggersOfJob(HbJob, ct)).Select(t => t.Key)], ct);
        var callsBefore = flaky.Proxy.ScheduleJobCalls;

        flaky.Proxy.FailScheduleJob = int.MaxValue;
        await flaky.Synchronizer.AdvanceAsync("hb", new TriggerKey("fired"), ct);
        await flaky.Synchronizer.AdvanceAsync("hb", new TriggerKey("fired"), ct);

        Assert.Equal(8, flaky.Proxy.ScheduleJobCalls - callsBefore);
        var alert = Assert.Single(flaky.Alerts.Recent, a => a.Kind == "routine.schedule-failed");
        Assert.Contains("after 4 attempts", alert.Message, StringComparison.Ordinal);
        Assert.Empty(await flaky.Real.GetTriggersOfJob(HbJob, ct));

        flaky.Proxy.FailScheduleJob = 0;
        await flaky.Synchronizer.RepairAsync(ct);

        Assert.Single(await flaky.Real.GetTriggersOfJob(HbJob, ct));
    }

    [Theory]
    [InlineData("* * * * * ?")]
    [InlineData("0 */1 * * * ?")]
    public void Planning_fails_closed_for_a_cron_that_is_too_frequent_whatever_put_it_there(string expression)
    {
        using var harness = new Harness();
        using var synchronizer = SynchronizerFor(harness);

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Cron(expression), DateTimeOffset.UtcNow));

        Assert.Contains("minimum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planning_fails_closed_for_an_invalid_cron_and_a_missing_or_short_interval()
    {
        using var harness = new Harness();
        using var synchronizer = SynchronizerFor(harness);

        Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Cron("garbage"), DateTimeOffset.UtcNow));
        Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Heartbeat() with { Interval = null }, DateTimeOffset.UtcNow));
        Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Heartbeat() with { Interval = TimeSpan.FromMinutes(1) }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Planning_fails_closed_instead_of_overflowing_for_an_enormous_interval_or_a_date_at_the_end_of_time()
    {
        using var harness = new Harness();
        using var synchronizer = SynchronizerFor(harness);

        var tooLong = Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Heartbeat(interval: TimeSpan.FromDays(10_675_199)), DateTimeOffset.UtcNow));
        Assert.Contains("maximum", tooLong.Message, StringComparison.Ordinal);

        var overflow = Assert.ThrowsAny<InvalidOperationException>(() => synchronizer.NextPlannedFire(Kit.Heartbeat(interval: TimeSpan.FromDays(366)), DateTimeOffset.MaxValue.AddDays(-1)));
        Assert.Contains("cannot be computed", overflow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planning_a_cron_at_the_end_of_time_never_throws_an_arithmetic_exception()
    {
        using var harness = new Harness();
        using var synchronizer = SynchronizerFor(harness);

        var ex = Record.Exception(() => synchronizer.NextPlannedFire(Kit.Cron("0 0 * * * ?"), DateTimeOffset.MaxValue.AddMinutes(-1)));

        Assert.True(ex is null or UnschedulableRoutineException, $"unexpected {ex}");
    }

    [Fact]
    public async Task The_registry_keeps_an_enormous_interval_out_so_the_scheduler_still_starts()
    {
        await using var harness = await QuartzHarness.StartAsync(Options);

        Assert.Throws<Validation.RoutineValidationException>(() => harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromDays(4_000_000)), Kit.User));
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Inner.Registry.Routines);
    }

    [Fact]
    public async Task The_registry_keeps_a_runaway_cron_out_so_the_scheduler_never_sees_it()
    {
        await using var harness = await QuartzHarness.StartAsync(Options);

        Assert.Throws<Validation.RoutineValidationException>(() => harness.Inner.Registry.AddOrUpdate(Kit.Cron("* * * * * ?"), Kit.User));
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Inner.Registry.Routines);
        Assert.Empty(await harness.Scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), TestContext.Current.CancellationToken));
    }

    private static ScheduleSynchronizer SynchronizerFor(Harness harness)
    {
        var scheduler = DispatchProxy.Create<IScheduler, FlakyScheduler>();
        return new ScheduleSynchronizer(harness.Registry, scheduler, harness.Dispatcher, harness.KillSwitch, harness.Options, harness.Alerts, harness.Time);
    }

    /// <summary>A real durable scheduler behind a proxy that can fail <c>ScheduleJob</c> on demand.</summary>
    private sealed class FlakyHarness : IDisposable
    {
        private readonly Harness _inner;

        private FlakyHarness(Harness inner, IScheduler real, FlakyScheduler proxy, ScheduleSynchronizer synchronizer)
        {
            _inner = inner;
            Real = real;
            Proxy = proxy;
            Synchronizer = synchronizer;
        }

        public Quartz.IScheduler Real { get; }

        public FlakyScheduler Proxy { get; }

        public ScheduleSynchronizer Synchronizer { get; }

        public Capybara.Scheduling.Lifecycle.RoutineRegistry Registry => _inner.Registry;

        public Capybara.Observability.Alerts.InMemoryAlertSink Alerts => _inner.Alerts;

        public static async Task<FlakyHarness> StartAsync(TempDirectory directory, int failures)
        {
            var options = Options(Path.Combine(directory.Path, "scheduling.sqlite"));
            var inner = new Harness(options);
            var real = await new Durable.QuartzSchedulerFactory(options).CreateSchedulerAsync(TestContext.Current.CancellationToken);
            var proxy = DispatchProxy.Create<IScheduler, FlakyScheduler>();
            var flaky = (FlakyScheduler)(object)proxy;
            flaky.Target = real;
            flaky.FailScheduleJob = failures;
            var synchronizer = new ScheduleSynchronizer(inner.Registry, proxy, inner.Dispatcher, inner.KillSwitch, options, inner.Alerts, inner.Time);
            return new FlakyHarness(inner, real, flaky, synchronizer);
        }

        public void Dispose()
        {
            Synchronizer.Dispose();
            Real.Shutdown(waitForJobsToComplete: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _inner.Dispose();
        }
    }

    /// <summary>Forwards every call to <see cref="Target"/>, except that <c>ScheduleJob</c> fails while <see cref="FailScheduleJob"/> is positive.</summary>
    public class FlakyScheduler : DispatchProxy
    {
        private int _calls;

        public IScheduler? Target { get; set; }

        public int FailScheduleJob { get; set; }

        public int ScheduleJobCalls => Volatile.Read(ref _calls);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IScheduler.ScheduleJob))
            {
                Interlocked.Increment(ref _calls);
                if (FailScheduleJob > 0)
                {
                    FailScheduleJob--;
                    throw new SchedulerException("simulated store failure");
                }
            }

            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
