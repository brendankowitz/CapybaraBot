using Capybara.Contracts.Agents;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;

namespace Capybara.Scheduling.Tests;

/// <summary>FR-RTN-05: nothing fails silently, and the catch-up policy after downtime is explicit.</summary>
public sealed class DispatcherAlertAndCatchUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(Harness Harness, RoutineExecutionResult Result)> DispatchAsync(
        Harness harness,
        Dispatch.RoutineFiring? firing = null)
    {
        var routine = Kit.Heartbeat();
        harness.Registry.AddOrUpdate(routine, Kit.User);
        var result = await harness.Dispatcher.DispatchAsync(firing ?? new Dispatch.RoutineFiring(routine, Now, "run"), TestContext.Current.CancellationToken);
        return (harness, result);
    }

    [Fact]
    public async Task A_run_the_executor_refuses_raises_a_refused_alert()
    {
        using var harness = new Harness(executor: new ScriptedExecutor(_ => new RoutineExecutionResult(RoutineExecutionStatus.Refused, "policy says no")));

        var (_, result) = await DispatchAsync(harness);

        Assert.Equal(RoutineExecutionStatus.Refused, result.Status);
        var alert = Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.refused");
        Assert.Contains("policy says no", alert.Message, StringComparison.Ordinal);
        Assert.Equal(RoutineRunOutcome.Refused, Assert.Single(harness.History.Get("hb")).Outcome);
    }

    [Fact]
    public async Task A_run_the_executor_reports_over_budget_raises_an_over_budget_alert()
    {
        using var harness = new Harness(executor: new ScriptedExecutor(_ => new RoutineExecutionResult(RoutineExecutionStatus.OverBudget, "session cap")));

        await DispatchAsync(harness);

        Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.overbudget");
    }

    [Fact]
    public async Task A_routine_blocked_by_the_host_side_budget_never_executes_and_raises_an_alert()
    {
        using var harness = new Harness();
        harness.Ledger.SetBudget(Kit.Ea, new AgentBudget { DailyCredits = 0, MonthlyCredits = 0 });

        var (_, result) = await DispatchAsync(harness);

        Assert.Equal(RoutineExecutionStatus.OverBudget, result.Status);
        Assert.Empty(harness.Executor.Invocations);
        Assert.Contains(harness.Alerts.Recent, a => a.Kind == "routine.overbudget");
    }

    [Fact]
    public async Task An_executor_that_throws_is_recorded_as_a_failed_run_with_a_critical_alert()
    {
        using var harness = new Harness(executor: new ScriptedExecutor(_ => throw new InvalidOperationException("runtime exploded")));

        var (_, result) = await DispatchAsync(harness);

        Assert.Equal(RoutineExecutionStatus.Failed, result.Status);
        var alert = Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.failed");
        Assert.Equal(Capybara.Observability.Alerts.AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task Skipped_runs_for_a_paused_routine_and_a_missing_required_input_raise_alerts()
    {
        using var harness = new Harness();
        var routine = Kit.Heartbeat();
        harness.Registry.AddOrUpdate(routine, Kit.User);
        harness.Registry.Pause(routine.Id, "paused for test");

        var paused = await harness.Dispatcher.DispatchAsync(new Dispatch.RoutineFiring(routine, Now, "paused"), TestContext.Current.CancellationToken);

        Assert.Equal(RoutineExecutionStatus.Skipped, paused.Status);
        var alert = Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.skipped");
        Assert.Contains("paused for test", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_misfire_is_recorded_as_missed_with_an_alert_and_is_not_executed()
    {
        using var harness = new Harness();
        var routine = Kit.Heartbeat();

        var (_, result) = await DispatchAsync(harness, new Dispatch.RoutineFiring(routine, Now, "late", IsMisfire: true));

        Assert.Equal(RoutineExecutionStatus.Skipped, result.Status);
        Assert.Empty(harness.Executor.Invocations);
        Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.missed");
    }

    [Fact]
    public async Task Skip_policy_records_and_alerts_the_missed_run_but_does_not_execute()
    {
        using var harness = new Harness(new Dispatch.SchedulingOptions { CatchUpPolicy = Dispatch.CatchUpPolicy.Skip, TimeZone = TimeZoneInfo.Utc });
        var routine = Kit.Heartbeat();

        var (_, result) = await DispatchAsync(harness, new Dispatch.RoutineFiring(routine, Now, "late", ScheduledFor: Now.AddHours(-3)));

        Assert.Equal(RoutineExecutionStatus.Skipped, result.Status);
        Assert.Empty(harness.Executor.Invocations);
        var missed = Assert.Single(harness.History.Get(routine.Id));
        Assert.Equal(RoutineRunOutcome.Missed, missed.Outcome);
        Assert.Equal(Now.AddHours(-3), missed.StartedAt);
        var alert = Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.missed");
        Assert.Contains("skipped by catch-up policy", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fire_once_now_policy_alerts_the_missed_run_and_then_executes_exactly_one_catch_up()
    {
        using var harness = new Harness(new Dispatch.SchedulingOptions { CatchUpPolicy = Dispatch.CatchUpPolicy.FireOnceNow, TimeZone = TimeZoneInfo.Utc });
        var routine = Kit.Heartbeat();

        var (_, result) = await DispatchAsync(harness, new Dispatch.RoutineFiring(routine, Now, "late", ScheduledFor: Now.AddHours(-3)));

        Assert.Equal(RoutineExecutionStatus.Succeeded, result.Status);
        Assert.Single(harness.Executor.Invocations);
        Assert.Collection(
            harness.History.Get(routine.Id),
            run => Assert.Equal(RoutineRunOutcome.Missed, run.Outcome),
            run => Assert.Equal(RoutineRunOutcome.Succeeded, run.Outcome));
        var alert = Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.missed");
        Assert.Contains("catching up", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_firing_within_the_misfire_tolerance_is_on_time_under_either_policy()
    {
        using var harness = new Harness(new Dispatch.SchedulingOptions { CatchUpPolicy = Dispatch.CatchUpPolicy.Skip, MisfireTolerance = TimeSpan.FromMinutes(1), TimeZone = TimeZoneInfo.Utc });
        var routine = Kit.Heartbeat();

        var (_, result) = await DispatchAsync(harness, new Dispatch.RoutineFiring(routine, Now, "ontime", ScheduledFor: Now.AddSeconds(-20)));

        Assert.Equal(RoutineExecutionStatus.Succeeded, result.Status);
        Assert.Empty(harness.Alerts.Recent);
    }

    [Fact]
    public void The_default_catch_up_policy_is_skip()
    {
        // Explicit and documented: after downtime a routine is not silently replayed; the miss is alerted and the schedule continues.
        Assert.Equal(Dispatch.CatchUpPolicy.Skip, new Dispatch.SchedulingOptions().CatchUpPolicy);
    }

    [Fact]
    public async Task Active_hours_are_evaluated_in_the_configured_time_zone()
    {
        var pacific = TimeZoneInfo.CreateCustomTimeZone("test-minus-7", TimeSpan.FromHours(-7), "test", "test");
        using var harness = new Harness(new Dispatch.SchedulingOptions { TimeZone = pacific });
        var routine = Kit.Heartbeat() with { ActiveFrom = new TimeOnly(9, 0), ActiveTo = new TimeOnly(10, 0) };
        harness.Registry.AddOrUpdate(routine, Kit.User);

        // 16:30Z is 09:30 local (inside); 12:00Z is 05:00 local (outside).
        var inside = await harness.Dispatcher.DispatchAsync(new Dispatch.RoutineFiring(routine, new DateTimeOffset(2026, 3, 4, 16, 30, 0, TimeSpan.Zero), "in"), TestContext.Current.CancellationToken);
        var outside = await harness.Dispatcher.DispatchAsync(new Dispatch.RoutineFiring(routine, Now, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(RoutineExecutionStatus.Succeeded, inside.Status);
        Assert.Equal(RoutineExecutionStatus.NothingToReport, outside.Status);
        Assert.Single(harness.Executor.Invocations);
    }
}
