using Capybara.Observability.Alerts;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Validation;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;
using SchedulingOptions = Capybara.Scheduling.Dispatch.SchedulingOptions;

namespace Capybara.Scheduling.Tests;

/// <summary>FR-RTN-04/06: lifecycle, auto-pause on a timer, and durable state.</summary>
public sealed class LifecycleAndDurabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_routine_nobody_ever_interacted_with_auto_pauses_after_the_configured_days()
    {
        var time = new FakeTimeProvider(Start);
        var alerts = new InMemoryAlertSink();
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        var pauser = new RoutineAutoPauser(registry, alerts, time, autoPauseDays: 3);

        await pauser.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState("hb").Runnable);

        time.Advance(TimeSpan.FromDays(2));
        await pauser.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState("hb").Runnable);

        time.Advance(TimeSpan.FromDays(2));
        await pauser.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState("hb").AutoPaused);
        Assert.Single(alerts.Recent, a => a.Kind == "routine.autopaused" && a.Message.Contains("confirm to resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interacting_with_the_owner_agent_keeps_its_routines_running()
    {
        var time = new FakeTimeProvider(Start);
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat("a"), Kit.User);
        registry.AddOrUpdate(Kit.Heartbeat("b") with { Owner = Capybara.Contracts.Agents.AgentId.Parse("code") }, Kit.User);
        var pauser = new RoutineAutoPauser(registry, new InMemoryAlertSink(), time, autoPauseDays: 3);
        await pauser.CheckAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromDays(2));
        pauser.RecordUserInteraction(Kit.Ea, Kit.User);
        time.Advance(TimeSpan.FromDays(2));
        await pauser.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(registry.GetState("a").Runnable);
        Assert.True(registry.GetState("b").AutoPaused);
    }

    [Fact]
    public async Task Resuming_needs_a_human_confirmation_and_restarts_the_inactivity_clock()
    {
        var time = new FakeTimeProvider(Start);
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        var pauser = new RoutineAutoPauser(registry, new InMemoryAlertSink(), time, autoPauseDays: 3);
        await pauser.CheckAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromDays(4));
        await pauser.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState("hb").AutoPaused);

        Assert.Throws<InvalidOperationException>(() => pauser.ConfirmResume("hb", RoutinePrincipal.Agent("ea")));
        pauser.ConfirmResume("hb", Kit.User);
        await pauser.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(registry.GetState("hb").Runnable);
    }

    [Fact]
    public async Task Interaction_times_survive_a_restart_so_restarting_cannot_postpone_the_pause()
    {
        using var directory = new TempDirectory();
        var db = Path.Combine(directory.Path, "state.sqlite");
        var time = new FakeTimeProvider(Start);
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        var before = new RoutineAutoPauser(registry, new InMemoryAlertSink(), time, autoPauseDays: 3, sqlitePath: db);
        before.RecordUserInteraction("hb", Kit.User);

        time.Advance(TimeSpan.FromDays(4));
        var afterRestart = new RoutineAutoPauser(registry, new InMemoryAlertSink(), time, autoPauseDays: 3, sqlitePath: db);
        await afterRestart.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(registry.GetState("hb").AutoPaused);
    }

    [Fact]
    public async Task The_hosted_timer_runs_the_auto_pause_check_without_anyone_calling_it()
    {
        var time = new FakeTimeProvider(Start);
        var alerts = new InMemoryAlertSink();
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        var pauser = new RoutineAutoPauser(registry, alerts, time, autoPauseDays: 3);
        var options = new SchedulingOptions { AutoPauseCheckInterval = TimeSpan.FromHours(1) };
        using var service = new AutoPauseHostedService(pauser, options, alerts, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 300 && !registry.GetState("hb").AutoPaused; i++)
        {
            time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(registry.GetState("hb").AutoPaused);
    }

    [Fact]
    public void Config_edits_to_the_enabled_flag_change_state_but_other_edits_keep_it()
    {
        var registry = new RoutineRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        registry.Pause("hb", "operator");

        registry.AddOrUpdate(Kit.Heartbeat() with { Prompt = "edited" }, Kit.User);
        Assert.True(registry.GetState("hb").Paused);

        registry.AddOrUpdate(Kit.Heartbeat() with { Enabled = false }, Kit.User);
        Assert.False(registry.GetState("hb").Enabled);

        registry.Remove("hb", Kit.User);
        Assert.Null(registry.Find("hb"));
        Assert.Equal(5, changes);
        Assert.Throws<InvalidOperationException>(() => registry.Remove("hb", RoutinePrincipal.Agent("ea")));
        Assert.Throws<KeyNotFoundException>(() => registry.Pause("hb", "gone"));
    }

    [Fact]
    public async Task Run_history_persists_across_instances_with_evidence_and_stays_bounded()
    {
        using var directory = new TempDirectory();
        var db = Path.Combine(directory.Path, "history.sqlite");
        var ct = TestContext.Current.CancellationToken;
        var first = new RoutineRunHistory(new InMemoryAlertSink(), db);
        for (var i = 0; i < RoutineLimits.RunHistoryDepth + 5; i++)
        {
            await first.RecordAsync(new RoutineRun("hb", Start.AddMinutes(i), RoutineRunOutcome.Succeeded, $"run {i}", 0.5m, ["source", "action"]), ct);
        }

        await first.RecordAsync(new RoutineRun("other", Start, RoutineRunOutcome.Failed, "boom"), ct);

        var alerts = new InMemoryAlertSink();
        var second = new RoutineRunHistory(alerts, db);
        var runs = second.Get("hb");

        Assert.Equal(RoutineLimits.RunHistoryDepth, runs.Count);
        Assert.Equal("run 5", runs[0].Summary);
        Assert.Equal($"run {RoutineLimits.RunHistoryDepth + 4}", runs[^1].Summary);
        Assert.Equal(0.5m, runs[0].CreditsSpent);
        Assert.Equal(["source", "action"], runs[0].Evidence!.ToArray());
        Assert.Equal(Start.AddMinutes(5), runs[0].StartedAt);
        var other = Assert.Single(second.Get("other"));
        Assert.Equal(RoutineRunOutcome.Failed, other.Outcome);
        Assert.Null(other.Evidence);
        Assert.Empty(second.Get("missing"));
    }

    [Fact]
    public async Task A_durable_history_still_raises_alerts_for_non_success()
    {
        using var directory = new TempDirectory();
        var alerts = new InMemoryAlertSink();
        var history = new RoutineRunHistory(alerts, Path.Combine(directory.Path, "h.sqlite"));

        await history.RecordAsync(new RoutineRun("hb", Start, RoutineRunOutcome.Missed, "down"), TestContext.Current.CancellationToken);

        Assert.Single(alerts.Recent, a => a.Kind == "routine.missed");
    }

    [Fact]
    public async Task A_history_write_failure_raises_a_scheduler_error_and_still_publishes_the_runs_own_alert()
    {
        using var directory = new TempDirectory();
        var db = Path.Combine(directory.Path, "h.sqlite");
        var alerts = new InMemoryAlertSink();
        var history = new RoutineRunHistory(alerts, db);
        File.Delete(db);
        Directory.CreateDirectory(db);

        await history.RecordAsync(new RoutineRun("hb", Start, RoutineRunOutcome.Refused, "nope"), TestContext.Current.CancellationToken);

        Assert.Single(alerts.Recent, a => a.Kind == "scheduler.error" && a.Message.Contains("'hb'", StringComparison.Ordinal));
        Assert.Single(alerts.Recent, a => a.Kind == "routine.refused");
    }

    [Fact]
    public async Task Credits_are_ledgered_even_when_the_history_cannot_be_written()
    {
        using var directory = new TempDirectory();
        var db = Path.Combine(directory.Path, "h.sqlite");
        var executor = new ScriptedExecutor(_ => new Execution.RoutineExecutionResult(Execution.RoutineExecutionStatus.Succeeded, "ok", CreditsUsed: 4m, Model: "auto"));
        using var harness = new Harness(executor: executor, historyPath: db);
        File.Delete(db);
        Directory.CreateDirectory(db);
        var routine = Kit.Heartbeat();
        harness.Registry.AddOrUpdate(routine, Kit.User);

        var result = await harness.Dispatcher.DispatchAsync(new RoutineFiring(routine, Start, "run"), TestContext.Current.CancellationToken);

        Assert.Equal(Execution.RoutineExecutionStatus.Succeeded, result.Status);
        Assert.Equal(4m, Assert.Single(harness.Ledger.Records).Credits);
        Assert.Contains(harness.Alerts.Recent, a => a.Kind == "scheduler.error");
    }

    [Fact]
    public void The_registry_refuses_a_routine_that_would_be_an_agents_fifty_first()
    {
        var registry = new RoutineRegistry();
        for (var i = 0; i < RoutineLimits.MaxRoutinesPerAgent; i++)
        {
            registry.AddOrUpdate(Kit.Heartbeat($"r{i}"), Kit.User);
        }

        var ex = Assert.Throws<RoutineValidationException>(() => registry.AddOrUpdate(Kit.Heartbeat("one-too-many"), Kit.User));

        Assert.Contains("limit", ex.Message, StringComparison.Ordinal);
        Assert.Null(registry.Find("one-too-many"));
        registry.AddOrUpdate(Kit.Heartbeat("r0") with { Prompt = "an edit is not a new routine" }, Kit.User);
        registry.AddOrUpdate(Kit.Heartbeat("other-agent") with { Owner = Capybara.Contracts.Agents.AgentId.Parse("code") }, Kit.User);
    }

    [Theory]
    [InlineData("* * * * * ?")]
    [InlineData("0 */1 * * * ?")]
    public void The_registry_refuses_a_cron_below_the_five_minute_minimum(string expression)
    {
        var registry = new RoutineRegistry();

        var ex = Assert.Throws<RoutineValidationException>(() => registry.AddOrUpdate(Kit.Cron(expression), Kit.User));

        Assert.Contains("minimum", ex.Message, StringComparison.Ordinal);
        Assert.Empty(registry.Routines);
    }

    [Fact]
    public void The_registry_refuses_other_invalid_routines_too()
    {
        var registry = new RoutineRegistry();

        Assert.Throws<RoutineValidationException>(() => registry.AddOrUpdate(Kit.Heartbeat(interval: TimeSpan.FromMinutes(1)), Kit.User));
        Assert.Throws<RoutineValidationException>(() => registry.AddOrUpdate(Kit.Event("github:push"), Kit.User));
        Assert.Throws<RoutineValidationException>(() => registry.AddOrUpdate(Kit.Heartbeat() with { BudgetCreditsPerRun = 0 }, Kit.User));
    }

    [Fact]
    public async Task Only_a_human_principal_can_change_routines_record_interactions_load_config_or_dry_run()
    {
        var agent = RoutinePrincipal.Agent("ea");
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        var pauser = new RoutineAutoPauser(harness.Registry, harness.Alerts, harness.Time);
        await using var quartz = await QuartzHarness.StartAsync(p => new SchedulingOptions { SQLitePath = p, TimeZone = TimeZoneInfo.Utc });
        using var loader = new RoutineConfigLoader(quartz.Inner.Registry, quartz.Synchronizer, quartz.Inner.Alerts, TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => harness.Registry.AddOrUpdate(Kit.Heartbeat("x"), agent));
        Assert.Throws<InvalidOperationException>(() => harness.Registry.Remove("hb", agent));
        Assert.Throws<InvalidOperationException>(() => harness.Registry.Resume("hb", agent));
        Assert.Throws<InvalidOperationException>(() => harness.Registry.Disable("hb", agent));
        Assert.Throws<InvalidOperationException>(() => pauser.RecordUserInteraction("hb", agent));
        Assert.Throws<InvalidOperationException>(() => pauser.RecordUserInteraction(Kit.Ea, agent));
        Assert.Throws<InvalidOperationException>(() => pauser.ConfirmResume("hb", agent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.ApplyAsync([Kit.Heartbeat()], new HashSet<Capybara.Contracts.Agents.AgentId> { Kit.Ea }, agent, TestContext.Current.CancellationToken));
        Assert.Empty(quartz.Inner.Registry.Routines);
        Assert.Throws<ArgumentException>(() => RoutinePrincipal.Human(" "));
        Assert.False(agent.IsHuman);
    }

    [Fact]
    public async Task Enabling_a_routine_restarts_its_inactivity_clock()
    {
        var time = new FakeTimeProvider(Start);
        var registry = new RoutineRegistry();
        var routine = Kit.Heartbeat() with { Enabled = false };
        registry.AddOrUpdate(routine, Kit.User);
        var pauser = new RoutineAutoPauser(registry, new InMemoryAlertSink(), time, autoPauseDays: 3);
        pauser.RecordUserInteraction("hb", Kit.User);
        time.Advance(TimeSpan.FromDays(10));

        registry.Enable("hb", registry.ProjectedMonthlyCredits(routine), Kit.User);
        await pauser.CheckAsync(TestContext.Current.CancellationToken);

        // The last interaction was ten days ago, but enabling is an interaction: without the restart this would auto-pause at once.
        Assert.True(registry.GetState("hb").Runnable);
    }

    [Fact]
    public void Resuming_restarts_the_inactivity_clock_before_the_routine_becomes_runnable_again()
    {
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        registry.AutoPause("hb", "test");
        var pauser = new RoutineAutoPauser(registry, new InMemoryAlertSink(), new FakeTimeProvider(Start));
        var order = new List<string>();
        registry.UserInteraction += (_, _) => order.Add("clock restarted");
        registry.Changed += (_, _) => order.Add("state changed");

        pauser.ConfirmResume("hb", Kit.User);

        Assert.Equal(["clock restarted", "state changed"], order);
    }
}
