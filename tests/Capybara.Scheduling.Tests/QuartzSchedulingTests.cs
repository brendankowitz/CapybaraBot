using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.DependencyInjection;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Triggers;
using Capybara.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Quartz;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;
namespace Capybara.Scheduling.Tests;

/// <summary>The durable Quartz integration: scheduling, per-fire jitter, kill switch, catch-up and config reload (FR-RTN-03/05).</summary>
public sealed class QuartzSchedulingTests
{
    private static readonly JobKey HbJob = new("hb", "ea");

    private static SchedulingOptions Options(string path, CatchUpPolicy policy = CatchUpPolicy.Skip) =>
        new() { SQLitePath = path, CatchUpPolicy = policy, TimeZone = TimeZoneInfo.Utc };

    private static async Task<IReadOnlyList<ITrigger>> TriggersAsync(QuartzHarness harness, JobKey? job = null) =>
        [.. await harness.Scheduler.GetTriggersOfJob(job ?? HbJob, TestContext.Current.CancellationToken)];

    /// <summary>Replaces the routine's pending trigger with one that fires immediately and was planned for <paramref name="plannedFor"/>.</summary>
    private static async Task FireNowPlannedForAsync(QuartzHarness harness, DateTimeOffset plannedFor)
    {
        var ct = TestContext.Current.CancellationToken;
        await harness.Scheduler.UnscheduleJobs([.. (await TriggersAsync(harness)).Select(t => t.Key)], ct);
        var trigger = TriggerBuilder.Create()
            .ForJob(HbJob)
            .WithIdentity("manual-chain", "ea")
            .UsingJobData("plannedFire", plannedFor.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .StartNow()
            .Build();
        await harness.Scheduler.ScheduleJob(trigger, new ScheduleJobOptions(), ct);
    }

    [Fact]
    public async Task Synchronizing_schedules_one_jittered_trigger_per_runnable_routine_and_is_idempotent()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat("off") with { Enabled = false }, Kit.User);
        harness.Inner.Registry.AddOrUpdate(Kit.Event("github:push repo=a/b"), Kit.User);

        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        var trigger = Assert.Single(await TriggersAsync(harness));
        var delay = trigger.StartTimeUtc - DateTimeOffset.UtcNow;
        Assert.InRange(delay.TotalMinutes, 10 * 0.9 - 0.1, 10 * 1.1 + 0.1);
        Assert.Empty(await TriggersAsync(harness, new JobKey("off", "ea")));
        Assert.True(await harness.Scheduler.Exists(new JobKey("off", "ea"), TestContext.Current.CancellationToken));
        Assert.False(await harness.Scheduler.Exists(new JobKey("evt", "ea"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_fire_gets_its_own_jitter_within_ten_percent_of_the_interval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await QuartzHarness.StartAsync(p => Options(p), time: time);
        var interval = TimeSpan.FromDays(1);
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(interval: interval), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        var delays = new List<TimeSpan>();

        for (var i = 0; i < 8; i++)
        {
            time.Advance(TimeSpan.FromMinutes(37));
            await harness.Scheduler.UnscheduleJobs([.. (await TriggersAsync(harness)).Select(t => t.Key)], TestContext.Current.CancellationToken);
            await harness.Synchronizer.AdvanceAsync("hb", new TriggerKey("fired"), TestContext.Current.CancellationToken);
            var next = Assert.Single(await TriggersAsync(harness));
            delays.Add(next.StartTimeUtc - time.GetUtcNow());
        }

        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, interval.TotalSeconds * 0.9, interval.TotalSeconds * 1.1));
        Assert.True(delays.Distinct().Count() > 1, "jitter should differ between fires, not only on the first start");
    }

    [Fact]
    public async Task A_cron_routine_is_scheduled_at_the_next_cron_time_in_the_configured_zone()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        harness.Inner.Registry.AddOrUpdate(Kit.Cron("0 0 */6 * * ?"), Kit.User);

        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        var trigger = Assert.Single(await TriggersAsync(harness, new JobKey("cron", "ea")));
        var next = trigger.StartTimeUtc;
        Assert.Equal(0, next.Hour % 6);
        Assert.Equal(0, next.Minute);
        Assert.InRange((next - DateTimeOffset.UtcNow).TotalHours, 0, 6.01);
    }

    [Fact]
    public async Task Firing_runs_the_routine_and_then_plans_the_next_fire()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromMinutes(5)), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        await harness.Scheduler.Start(TestContext.Current.CancellationToken);

        await FireNowPlannedForAsync(harness, DateTimeOffset.UtcNow);

        await Kit.EventuallyAsync(() => ValueTask.FromResult(harness.Inner.Executor.Invocations.Count == 1), "routine executed");
        await Kit.EventuallyAsync(
            async () => await TriggersAsync(harness) is [{ Key.Name: not "manual-chain" } next] && next.StartTimeUtc > DateTimeOffset.UtcNow.AddMinutes(4),
            "next fire planned ~5 minutes out");
        Assert.Empty(harness.Inner.Alerts.Recent);
    }

    [Fact]
    public async Task After_downtime_the_skip_policy_alerts_the_missed_run_and_the_schedule_continues()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p, CatchUpPolicy.Skip));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromMinutes(5)), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        await harness.Scheduler.Start(TestContext.Current.CancellationToken);

        await FireNowPlannedForAsync(harness, DateTimeOffset.UtcNow.AddHours(-6));

        await Kit.EventuallyAsync(() => ValueTask.FromResult(harness.Inner.Alerts.Recent.Any(a => a.Kind == "routine.missed")), "missed-run alert");
        await Kit.EventuallyAsync(async () => await TriggersAsync(harness) is [{ Key.Name: not "manual-chain" }], "schedule continues");
        Assert.Empty(harness.Inner.Executor.Invocations);
    }

    [Fact]
    public async Task After_downtime_the_fire_once_now_policy_alerts_and_runs_one_catch_up()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p, CatchUpPolicy.FireOnceNow));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromMinutes(5)), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        await harness.Scheduler.Start(TestContext.Current.CancellationToken);

        await FireNowPlannedForAsync(harness, DateTimeOffset.UtcNow.AddHours(-6));

        await Kit.EventuallyAsync(() => ValueTask.FromResult(harness.Inner.Executor.Invocations.Count == 1), "one catch-up run");
        await Kit.EventuallyAsync(async () => await TriggersAsync(harness) is [{ Key.Name: not "manual-chain" }], "schedule continues");
        Assert.Single(harness.Inner.Alerts.Recent, a => a.Kind == "routine.missed");
        Assert.Single(harness.Inner.Executor.Invocations);
    }

    [Fact]
    public async Task Engaging_the_kill_switch_pauses_quartz_and_releasing_it_resumes()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);
        await harness.Scheduler.Start(TestContext.Current.CancellationToken);
        var key = Assert.Single(await TriggersAsync(harness)).Key;
        Assert.Equal(TriggerState.Normal, await harness.Scheduler.GetTriggerState(key, TestContext.Current.CancellationToken));

        harness.Inner.KillSwitch.Engage("test");
        await Kit.EventuallyAsync(async () => await harness.Scheduler.GetTriggerState(key, TestContext.Current.CancellationToken) == TriggerState.Paused, "trigger paused");

        harness.Inner.KillSwitch.Release(ControlSurface.Desktop);
        await Kit.EventuallyAsync(async () => await harness.Scheduler.GetTriggerState(key, TestContext.Current.CancellationToken) == TriggerState.Normal, "trigger resumed");
    }

    [Fact]
    public async Task A_kill_switch_already_engaged_at_startup_starts_the_scheduler_paused()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        harness.Inner.KillSwitch.Engage("engaged before start");

        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        var key = Assert.Single(await TriggersAsync(harness)).Key;
        Assert.Equal(TriggerState.Paused, await harness.Scheduler.GetTriggerState(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registry_changes_resynchronize_the_scheduler_without_a_restart()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        var registry = harness.Inner.Registry;
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await harness.Synchronizer.SynchronizeAsync(TestContext.Current.CancellationToken);

        registry.Pause("hb", "operator");
        await Kit.EventuallyAsync(async () => (await TriggersAsync(harness)).Count == 0, "paused routine unscheduled");

        registry.Resume("hb", Kit.User);
        await Kit.EventuallyAsync(async () => (await TriggersAsync(harness)).Count == 1, "resumed routine rescheduled");

        registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromDays(2)), Kit.User);
        await Kit.EventuallyAsync(
            async () => await TriggersAsync(harness) is [var next] && next.StartTimeUtc > DateTimeOffset.UtcNow.AddDays(1.5),
            "edited interval replaces the old trigger");

        registry.Remove("hb", Kit.User);
        await Kit.EventuallyAsync(async () => !await harness.Scheduler.Exists(HbJob, TestContext.Current.CancellationToken), "removed routine's job deleted");
    }

    [Fact]
    public async Task The_config_loader_populates_the_registry_schedules_and_projects_cost()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        using var loader = new RoutineConfigLoader(harness.Inner.Registry, harness.Synchronizer, harness.Inner.Alerts, TimeProvider.System);
        var known = new HashSet<AgentId> { Kit.Ea };
        var ct = TestContext.Current.CancellationToken;

        var first = await loader.ApplyAsync([Kit.Heartbeat(), Kit.Cron("0 0 */2 * * ?") with { BudgetCreditsPerRun = 2m }, Kit.Event("github:push repo=a/b")], known, Kit.User, ct);

        Assert.True(first.Applied);
        Assert.Equal(["hb", "cron", "evt"], first.Added);
        Assert.Equal(360 * 2m, first.ProjectedMonthlyCredits["cron"]);
        Assert.Single(await TriggersAsync(harness));
        Assert.Single(await TriggersAsync(harness, new JobKey("cron", "ea")));

        var second = await loader.ApplyAsync([Kit.Heartbeat() with { Prompt = "changed" }, Kit.Event("github:push repo=a/b")], known, Kit.User, ct);

        Assert.Equal(["hb"], second.Updated);
        Assert.Equal(["cron"], second.Removed);
        Assert.Null(harness.Inner.Registry.Find("cron"));
        Assert.False(await harness.Scheduler.Exists(new JobKey("cron", "ea"), ct));
    }

    [Fact]
    public async Task An_invalid_routine_config_is_rejected_whole_with_an_alert_and_the_previous_routines_stay()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        using var loader = new RoutineConfigLoader(harness.Inner.Registry, harness.Synchronizer, harness.Inner.Alerts, TimeProvider.System);
        var known = new HashSet<AgentId> { Kit.Ea };
        var ct = TestContext.Current.CancellationToken;
        await loader.ApplyAsync([Kit.Heartbeat()], known, Kit.User, ct);

        var rejected = await loader.ApplyAsync([Kit.Heartbeat() with { Prompt = "edited" }, Kit.Cron("0 */1 * * * ?")], known, Kit.User, ct);

        Assert.False(rejected.Applied);
        Assert.NotEmpty(rejected.Errors);
        Assert.Equal("check", harness.Inner.Registry.Find("hb")!.Prompt);
        Assert.Null(harness.Inner.Registry.Find("cron"));
        Assert.Single(harness.Inner.Alerts.Recent, a => a.Kind == "routine.config-rejected");
    }

    [Fact]
    public async Task The_config_loader_leaves_routines_it_did_not_load_alone()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        using var loader = new RoutineConfigLoader(harness.Inner.Registry, harness.Synchronizer, harness.Inner.Alerts, TimeProvider.System);
        harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat("manual"), Kit.User);

        await loader.ApplyAsync([Kit.Heartbeat()], new HashSet<AgentId> { Kit.Ea }, Kit.User, TestContext.Current.CancellationToken);
        await loader.ApplyAsync([], new HashSet<AgentId> { Kit.Ea }, Kit.User, TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Inner.Registry.Find("manual"));
        Assert.Null(harness.Inner.Registry.Find("hb"));
    }

    [Fact]
    public async Task Routines_not_loaded_from_config_count_towards_the_per_agent_limit_and_nothing_is_half_applied()
    {
        await using var harness = await QuartzHarness.StartAsync(p => Options(p));
        using var loader = new RoutineConfigLoader(harness.Inner.Registry, harness.Synchronizer, harness.Inner.Alerts, TimeProvider.System);
        for (var i = 0; i < 10; i++)
        {
            harness.Inner.Registry.AddOrUpdate(Kit.Heartbeat($"manual{i}"), Kit.User);
        }

        var declared = Enumerable.Range(0, 45).Select(i => Kit.Heartbeat($"cfg{i}")).ToList();
        var result = await loader.ApplyAsync(declared, new HashSet<AgentId> { Kit.Ea }, Kit.User, TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Equal(10, harness.Inner.Registry.Routines.Count);
        Assert.Single(harness.Inner.Alerts.Recent, a => a.Kind == "routine.config-rejected");
    }

    [Fact]
    public async Task A_host_with_external_ledger_kill_switch_alerts_and_executor_resolves_and_runs_the_scheduler()
    {
        using var directory = new TempDirectory();
        var alerts = new InMemoryAlertSink();
        var services = new ServiceCollection();
        services.AddSingleton<IAlertSink>(alerts);
        services.AddSingleton(sp => new CostLedger(sp.GetRequiredService<IAlertSink>(), TimeProvider.System));
        services.AddSingleton(new KillSwitch(TimeProvider.System));
        services.AddSingleton<IRoutineExecutor>(new ScriptedExecutor());
        services.AddCapybaraScheduling(Options(Path.Combine(directory.Path, "scheduling.sqlite")));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().ToList();
        var scheduler = Assert.Single(hosted.OfType<SchedulerHostedService>());
        Assert.Single(hosted.OfType<AutoPauseHostedService>());
        Assert.Single(hosted.OfType<ScheduleRepairHostedService>());
        Assert.Single(hosted.OfType<EventSourceHostedService>());
        Assert.Same(provider.GetRequiredService<CostLedger>(), provider.GetRequiredService<CostLedger>());
        Assert.Same(alerts, provider.GetRequiredService<IAlertSink>());
        Assert.NotNull(provider.GetRequiredService<RoutineDispatcher>());
        Assert.NotNull(provider.GetRequiredService<EventRouter>());
        Assert.NotNull(provider.GetRequiredService<RoutineConfigLoader>());

        provider.GetRequiredService<RoutineRegistry>().AddOrUpdate(Kit.Heartbeat(), Kit.User);
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        var quartz = provider.GetRequiredService<IScheduler>();
        var trigger = Assert.Single(await quartz.GetTriggersOfJob(HbJob, TestContext.Current.CancellationToken));
        Assert.Equal(TriggerState.Normal, await quartz.GetTriggerState(trigger.Key, TestContext.Current.CancellationToken));
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Concurrent_stops_both_wait_for_the_one_scheduler_shutdown()
    {
        // A test host stops twice at once (its own StopAsync and the app's RunAsync); a caller that returned early
        // would dispose the container under a scheduler still shutting down.
        using var directory = new TempDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IAlertSink>(new InMemoryAlertSink());
        services.AddSingleton(sp => new CostLedger(sp.GetRequiredService<IAlertSink>(), TimeProvider.System));
        services.AddSingleton(new KillSwitch(TimeProvider.System));
        services.AddSingleton<IRoutineExecutor>(new ScriptedExecutor());
        services.AddCapybaraScheduling(Options(Path.Combine(directory.Path, "scheduling.sqlite")));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().OfType<SchedulerHostedService>().Single();
        var quartz = provider.GetRequiredService<IScheduler>();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        var first = hosted.StopAsync(TestContext.Current.CancellationToken);
        var second = hosted.StopAsync(TestContext.Current.CancellationToken);
        await second;

        Assert.Equal(SchedulerStatus.Shutdown, quartz.Status);
        await first;
    }

    [Fact]
    public void Adding_scheduling_twice_does_not_duplicate_registrations()
    {
        var services = new ServiceCollection();
        services.AddCapybaraScheduling(new SchedulingOptions());
        services.AddCapybaraScheduling(new SchedulingOptions());

        Assert.Single(services, d => d.ServiceType == typeof(RoutineDispatcher));
        Assert.Single(services, d => d.ServiceType == typeof(IScheduler));
        Assert.Single(services, d => d.ImplementationType == typeof(SchedulerHostedService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAlertSink) || d.ServiceType == typeof(CostLedger) || d.ServiceType == typeof(KillSwitch));
    }
}
