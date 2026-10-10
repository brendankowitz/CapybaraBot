using System.Text.Json;
using Capybara.Contracts.Messaging;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Triggers;

namespace Capybara.Scheduling.Tests;

/// <summary>FR-RTN-01/07 event routing and FR-RTN-08 dry run.</summary>
public sealed class EventRoutingAndDryRunTests
{
    private const string PullRequestFilter = "github:pull_request.opened repo=owner/name";

    private static RoutineEventPayload PullRequest(string repo = "owner/name", Taint taint = Taint.Clean) =>
        new("github", "pull_request.opened", JsonSerializer.Serialize(new { repo, action = "opened" }), taint);

    [Fact]
    public async Task Only_routines_whose_narrow_filter_matches_are_dispatched_and_the_payload_is_tainted()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter, "pr"), Kit.User);
        harness.Registry.AddOrUpdate(Kit.Event("github:issues.opened repo=owner/name", "issue"), Kit.User);
        harness.Registry.AddOrUpdate(Kit.Event("github:pull_request.opened repo=other/repo", "other-repo"), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        var results = await router.RouteAsync(PullRequest(taint: Taint.Clean), TestContext.Current.CancellationToken);

        var routed = Assert.Single(results);
        Assert.Equal("pr", routed.RoutineId);
        var invocation = Assert.Single(harness.Executor.Invocations);
        Assert.Equal(RoutineTriggerKind.Event, invocation.TriggerKind);
        Assert.Equal(SessionTarget.IsolatedTask, invocation.SessionTarget);
        Assert.Equal(Taint.Tainted, invocation.EventPayload?.Taint);
    }

    [Fact]
    public async Task An_event_nobody_listens_for_dispatches_nothing_and_is_not_an_error()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        var results = await router.RouteAsync(new RoutineEventPayload("github", "push", "{\"repo\":\"owner/name\"}"), TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Empty(harness.Executor.Invocations);
        Assert.Empty(harness.Alerts.Recent);
    }

    [Fact]
    public async Task Heartbeat_and_cron_routines_are_never_event_targets()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Heartbeat(), Kit.User);
        harness.Registry.AddOrUpdate(Kit.Cron("0 0 * * * ?"), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        Assert.Empty(await router.RouteAsync(PullRequest(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task Malformed_or_non_object_payloads_are_handled_without_dispatching(string json)
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        var results = await router.RouteAsync(new RoutineEventPayload("github", "pull_request.opened", json), TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Empty(harness.Executor.Invocations);
    }

    [Fact]
    public async Task Invalid_json_raises_an_alert_instead_of_vanishing()
    {
        using var harness = new Harness();
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        await router.RouteAsync(new RoutineEventPayload("github", "push", "{broken"), TestContext.Current.CancellationToken);

        Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.event-rejected");
    }

    [Fact]
    public async Task Oversized_payloads_are_dropped_with_an_alert()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);
        var huge = "{\"repo\":\"owner/name\",\"pad\":\"" + new string('x', WebhookEventSource.MaxPayloadBytes) + "\"}";

        var results = await router.RouteAsync(new RoutineEventPayload("github", "pull_request.opened", huge), TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Contains(harness.Alerts.Recent, a => a.Kind == "routine.event-rejected" && a.Message.Contains("cap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_matching_event_for_a_disabled_routine_is_recorded_as_skipped_and_alerted()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter) with { Enabled = false }, Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);

        var results = await router.RouteAsync(PullRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(RoutineExecutionStatus.Skipped, Assert.Single(results).Result.Status);
        Assert.Empty(harness.Executor.Invocations);
        Assert.Single(harness.Alerts.Recent, a => a.Kind == "routine.skipped");
    }

    [Fact]
    public async Task An_attached_source_is_routed_and_detach_stops_routing()
    {
        using var harness = new Harness();
        harness.Registry.AddOrUpdate(Kit.Event(PullRequestFilter), Kit.User);
        var router = new EventRouter(harness.Registry, harness.Dispatcher, harness.Alerts, harness.Time);
        await using var source = new ManualSource();
        router.Attach(source);

        source.Raise(PullRequest());
        await Kit.EventuallyAsync(() => ValueTask.FromResult(harness.Executor.Invocations.Count == 1), "attached source routed");

        router.Detach(source);
        source.Raise(PullRequest());
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(harness.Executor.Invocations);
    }

    [Fact]
    public async Task Dry_run_executes_a_disabled_routine_flags_the_invocation_and_keeps_the_denied_approvals_as_evidence()
    {
        var executor = new ScriptedExecutor(i => new RoutineExecutionResult(
            RoutineExecutionStatus.Succeeded,
            "would have sent",
            new RoutineEvidence(ActionLog: ["drafted reply"], DeniedApprovals: ["mail.send"])));
        using var harness = new Harness(executor: executor);
        var routine = Kit.Heartbeat() with { Enabled = false };
        harness.Registry.AddOrUpdate(routine, Kit.User);

        var result = await harness.Dispatcher.DryRunAsync(routine.Id, Kit.User, TestContext.Current.CancellationToken);

        Assert.Equal(RoutineExecutionStatus.Succeeded, result.Status);
        Assert.True(Assert.Single(executor.Invocations).DryRun);
        var run = Assert.Single(harness.History.Get(routine.Id));
        Assert.StartsWith("[dry run]", run.Summary, StringComparison.Ordinal);
        Assert.Contains("denied in dry run: mail.send", run.Evidence!);
        Assert.Contains("drafted reply", run.Evidence!);
    }

    [Fact]
    public async Task Dry_run_still_honours_the_kill_switch_and_unknown_routines_are_rejected()
    {
        using var harness = new Harness();
        var routine = Kit.Heartbeat();
        harness.Registry.AddOrUpdate(routine, Kit.User);
        harness.KillSwitch.Engage("stop");

        var result = await harness.Dispatcher.DryRunAsync(routine.Id, Kit.User, TestContext.Current.CancellationToken);

        Assert.Equal(RoutineExecutionStatus.Skipped, result.Status);
        Assert.Empty(harness.Executor.Invocations);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Dispatcher.DryRunAsync("nope", Kit.User, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => { _ = harness.Dispatcher.DryRunAsync(routine.Id, RoutinePrincipal.Agent("ea"), TestContext.Current.CancellationToken); });
    }

    [Fact]
    public async Task A_silent_heartbeat_dry_run_is_recorded_so_the_tester_sees_an_answer()
    {
        using var harness = new Harness(executor: new ScriptedExecutor(_ => new RoutineExecutionResult(RoutineExecutionStatus.NothingToReport, "nothing new")));
        var routine = Kit.Heartbeat();
        harness.Registry.AddOrUpdate(routine, Kit.User);

        await harness.Dispatcher.DryRunAsync(routine.Id, Kit.User, TestContext.Current.CancellationToken);
        await harness.Dispatcher.DispatchAsync(new Dispatch.RoutineFiring(routine, DateTimeOffset.UtcNow, "real"), TestContext.Current.CancellationToken);

        var run = Assert.Single(harness.History.Get(routine.Id));
        Assert.Equal(RoutineRunOutcome.Succeeded, run.Outcome);
        Assert.Contains("dry run", run.Summary, StringComparison.Ordinal);
    }

    private sealed class ManualSource : IEventTriggerSource
    {
        public event EventHandler<RoutineEventPayload>? Fired;

        public void Raise(RoutineEventPayload payload) => Fired?.Invoke(this, payload);

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
