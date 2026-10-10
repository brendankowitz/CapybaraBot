using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-AGT-04 (reconcile after crash) and FR-AGT-09 (self-test fails closed, reruns after restart).</summary>
public sealed class SelfTestAndRecoveryTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly InMemoryAlertSink _alerts = new();
    private readonly KillSwitch _killSwitch;
    private readonly TempDirectory _state = new();

    public SelfTestAndRecoveryTests() => _killSwitch = new KillSwitch(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SelfTestGuard Guard() => new(_killSwitch, _alerts, _time, NullLogger<SelfTestGuard>.Instance);

    private EventLogReconciler Reconciler() =>
        new(_alerts, _time, new CopilotRuntimeOptions { StateRoot = _state.Path }, NullLogger<EventLogReconciler>.Instance);

    [Fact]
    public async Task Failed_self_test_pauses_the_agent_and_alerts()
    {
        var runtime = new ScriptedRuntime { SelfTest = new SelfTestResult(false, ["memory_read"], ["bash"]) };

        Assert.False(await Guard().VerifyAsync(runtime, Ct));

        Assert.True(_killSwitch.IsBlocked(runtime.Agent));
        var alert = Assert.Single(_alerts.Recent);
        Assert.Equal(SelfTestGuard.AlertKind, alert.Kind);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task Self_test_that_cannot_run_also_fails_closed()
    {
        var runtime = new ScriptedRuntime { SelfTestThrows = true };

        Assert.False(await Guard().VerifyAsync(runtime, Ct));
        Assert.True(_killSwitch.IsBlocked(runtime.Agent));
    }

    [Fact]
    public async Task Passing_self_test_leaves_the_agent_runnable()
    {
        var runtime = new ScriptedRuntime();

        Assert.True(await Guard().VerifyAsync(runtime, Ct));
        Assert.False(_killSwitch.IsBlocked(runtime.Agent));
        Assert.Empty(_alerts.Recent);
    }

    [Fact]
    public async Task Qualified_mcp_names_match_bare_runtime_names_and_tool_search_is_optional()
    {
        // AvailableTools is source-qualified (mcp:srv-tool) but the runtime reports srv-tool.
        var runtime = new ScriptedRuntime
        {
            SelfTest = new SelfTestResult(false, ["mcp:workiq-ask", "custom:list_agents"], ["list_agents", "tool_search_tool", "workiq-ask"]),
        };

        Assert.True(await Guard().VerifyAsync(runtime, Ct));

        var stillWrong = new ScriptedRuntime { SelfTest = new SelfTestResult(false, ["mcp:workiq-ask"], ["workiq-other"]) };
        Assert.False(await Guard().VerifyAsync(stillWrong, Ct));
    }

    [Fact]
    public async Task Supervisor_reconciles_then_reruns_the_self_test_after_a_restart()
    {
        var runtime = new ScriptedRuntime { Healthy = false, SelfTest = new SelfTestResult(false, [], ["bash"]) };
        var reconciler = new RecordingReconciler(runtime);
        var supervisor = new RuntimeSupervisor(
            [runtime], _alerts, new SupervisorOptions(), _time, NullLogger<RuntimeSupervisor>.Instance, Guard(), reconciler);

        await supervisor.CheckOnceAsync(Ct);

        Assert.Equal(1, runtime.Restarts);
        Assert.Equal(["reconcile", "selftest"], runtime.Calls);
        Assert.True(_killSwitch.IsBlocked(runtime.Agent));
    }

    [Fact]
    public async Task Supervisor_does_not_self_test_a_runtime_whose_restart_failed()
    {
        var runtime = new ScriptedRuntime { Healthy = false, RestartThrows = true };
        var supervisor = new RuntimeSupervisor(
            [runtime], _alerts, new SupervisorOptions(), _time, NullLogger<RuntimeSupervisor>.Instance, Guard(), new RecordingReconciler(runtime));

        await supervisor.CheckOnceAsync(Ct);

        Assert.Empty(runtime.Calls);
        Assert.False(_killSwitch.IsBlocked(runtime.Agent));
    }

    [Fact]
    public async Task Failing_reconciler_does_not_skip_the_self_test()
    {
        var runtime = new ScriptedRuntime { Healthy = false };
        var supervisor = new RuntimeSupervisor(
            [runtime], _alerts, new SupervisorOptions(), _time, NullLogger<RuntimeSupervisor>.Instance, Guard(), new RecordingReconciler(runtime, throws: true));

        await supervisor.CheckOnceAsync(Ct);

        Assert.Equal(["reconcile", "selftest"], runtime.Calls);
    }

    [Fact]
    public void Finished_turn_is_completed()
    {
        var recovery = TurnRecovery.Analyze(
        [
            User("i1"),
            TurnStart("t1"),
            Assistant(toolRequests: true),
            ToolStart("c1", "web_fetch"),
            ToolComplete("c1"),
            TurnEnd("t1"),
            TurnStart("t2"),
            Assistant(toolRequests: false),
            TurnEnd("t2"),
        ]);

        Assert.Equal(TurnRecoveryState.Completed, recovery.State);
        Assert.Equal("i1", recovery.InteractionId);
    }

    [Fact]
    public void Turn_cut_off_during_a_tool_call_is_interrupted_and_names_the_tool()
    {
        var recovery = TurnRecovery.Analyze(
        [
            User("old"), TurnStart("t0"), Assistant(toolRequests: false), TurnEnd("t0"),
            User("i2"), TurnStart("t1"), Assistant(toolRequests: true), ToolStart("c1", "email_send"),
        ]);

        Assert.Equal(TurnRecoveryState.Interrupted, recovery.State);
        Assert.Equal("i2", recovery.InteractionId);
        Assert.Equal(["email_send"], recovery.UnfinishedTools);
    }

    [Fact]
    public void Tool_result_the_model_never_answered_is_interrupted()
    {
        var recovery = TurnRecovery.Analyze(
            [User("i3"), TurnStart("t1"), Assistant(toolRequests: true), ToolStart("c1", "web_fetch"), ToolComplete("c1"), TurnEnd("t1")]);

        Assert.Equal(TurnRecoveryState.Interrupted, recovery.State);
        Assert.Empty(recovery.UnfinishedTools);
    }

    [Fact]
    public void Aborted_and_empty_sessions_are_not_interrupted()
    {
        Assert.Equal(TurnRecoveryState.Aborted, TurnRecovery.Analyze([User("i4"), TurnStart("t1"), new AbortEvent { Data = new AbortData { Reason = AbortReason.UserAbort } }]).State);
        Assert.Equal(TurnRecoveryState.NoTurn, TurnRecovery.Analyze([]).State);
    }

    [Fact]
    public async Task Event_log_reconciler_alerts_on_an_interrupted_turn()
    {
        var runtime = new HistoryRuntime([User("i5"), TurnStart("t1"), ToolStart("c9", "calendar_write")]);
        var reconciler = Reconciler();

        var recovery = await reconciler.ReconcileAsync(runtime, Ct);

        Assert.Equal(TurnRecoveryState.Interrupted, recovery.State);
        var alert = Assert.Single(_alerts.Recent);
        Assert.Equal(EventLogReconciler.AlertKind, alert.Kind);
        Assert.Contains("calendar_write", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_log_reconciler_reports_unavailable_without_an_event_history()
    {
        var reconciler = Reconciler();

        var recovery = await reconciler.ReconcileAsync(new ScriptedRuntime(), Ct);

        Assert.Equal(TurnRecoveryState.Unavailable, recovery.State);
        Assert.Empty(_alerts.Recent);
    }

    [Fact]
    public async Task Same_interrupted_turn_is_alerted_once_across_restarts()
    {
        var runtime = new HistoryRuntime([User("i6"), TurnStart("t1"), ToolStart("c1", "email_send")]);

        await Reconciler().ReconcileAsync(runtime, Ct);
        await Reconciler().ReconcileAsync(runtime, Ct); // a new instance = a process restart

        Assert.Single(_alerts.Recent);
        Assert.True(File.Exists(Path.Combine(_state.Path, "agents", "ea", "host", EventLogReconciler.FileName)));

        var next = new HistoryRuntime([User("i6"), TurnStart("t1"), ToolStart("c1", "email_send"), User("i7"), TurnStart("t2")]);
        await Reconciler().ReconcileAsync(next, Ct);
        Assert.Equal(2, _alerts.Recent.Count);
    }

    [Fact]
    public async Task Aborted_turn_with_unfinished_tools_is_alerted()
    {
        var runtime = new HistoryRuntime(
            [User("i8"), TurnStart("t1"), ToolStart("c1", "git_push"), new AbortEvent { Data = new AbortData { Reason = AbortReason.UserAbort } }]);

        var recovery = await Reconciler().ReconcileAsync(runtime, Ct);

        Assert.Equal(TurnRecoveryState.Aborted, recovery.State);
        Assert.Contains("git_push", Assert.Single(_alerts.Recent).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_alert_is_retried_on_the_next_start()
    {
        var runtime = new HistoryRuntime([User("i9"), TurnStart("t1")]);
        var failing = new EventLogReconciler(new ThrowingAlertSink(), _time, new CopilotRuntimeOptions { StateRoot = _state.Path }, NullLogger<EventLogReconciler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ReconcileAsync(runtime, Ct));
        await Reconciler().ReconcileAsync(runtime, Ct);

        Assert.Single(_alerts.Recent);
    }

    [Fact]
    public void Supervisor_and_hosted_service_degrade_loudly_without_their_dependencies()
    {
        var logger = new CapturingLogger<RuntimeSupervisor>();

        _ = new RuntimeSupervisor([], _alerts, new SupervisorOptions(), _time, logger);

        Assert.Equal(2, logger.Critical.Count);
        Assert.Contains(logger.Critical, m => m.Contains("self-test", StringComparison.Ordinal));
        Assert.Contains(logger.Critical, m => m.Contains("reconciliation", StringComparison.Ordinal));

        var wired = new CapturingLogger<RuntimeSupervisor>();
        _ = new RuntimeSupervisor([], _alerts, new SupervisorOptions(), _time, wired, Guard(), Reconciler());
        Assert.Empty(wired.Critical);
    }

    public void Dispose()
    {
        _killSwitch.Dispose();
        _state.Dispose();
    }

    private sealed class ThrowingAlertSink : IAlertSink
    {
        public ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("alert channel down");
    }

    private static UserMessageEvent User(string interaction) => new() { Data = new UserMessageData { Content = "do it", InteractionId = interaction } };

    private static AssistantTurnStartEvent TurnStart(string turn) => new() { Data = new AssistantTurnStartData { TurnId = turn } };

    private static AssistantTurnEndEvent TurnEnd(string turn) => new() { Data = new AssistantTurnEndData { TurnId = turn } };

    private static AssistantMessageEvent Assistant(bool toolRequests) => new()
    {
        Data = new AssistantMessageData { MessageId = Guid.NewGuid().ToString("N"), Content = "ok", ToolRequests = toolRequests ? [new AssistantMessageToolRequest { Name = "web_fetch", ToolCallId = "c1" }] : [] },
    };

    private static ToolExecutionStartEvent ToolStart(string call, string tool) => new() { Data = new ToolExecutionStartData { ToolCallId = call, ToolName = tool } };

    private static ToolExecutionCompleteEvent ToolComplete(string call) => new() { Data = new ToolExecutionCompleteData { ToolCallId = call, Success = true } };

    private class ScriptedRuntime : ISupervisedRuntime
    {
        public AgentId Agent { get; } = AgentId.Parse("ea");

        public bool Healthy { get; init; } = true;

        public bool RestartThrows { get; init; }

        public bool SelfTestThrows { get; init; }

        public SelfTestResult SelfTest { get; init; } = new(true, [], []);

        public int Restarts { get; private set; }

        public List<string> Calls { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Healthy);

        public Task RestartAsync(CancellationToken cancellationToken)
        {
            Restarts++;
            return RestartThrows ? throw new InvalidOperationException("still broken") : Task.CompletedTask;
        }

        public Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken)
        {
            Calls.Add("selftest");
            return SelfTestThrows ? throw new InvalidOperationException("session would not resume") : Task.FromResult(SelfTest);
        }
    }

    private sealed class HistoryRuntime(IReadOnlyList<SessionEvent> events) : ScriptedRuntime, ISessionEventHistory
    {
        public Task<IReadOnlyList<SessionEvent>> GetMainSessionEventsAsync(CancellationToken cancellationToken) => Task.FromResult(events);
    }

    private sealed class RecordingReconciler(ScriptedRuntime runtime, bool throws = false) : ISessionReconciler
    {
        public Task<TurnRecovery> ReconcileAsync(ISupervisedRuntime supervised, CancellationToken cancellationToken)
        {
            runtime.Calls.Add("reconcile");
            return throws ? throw new InvalidOperationException("event log unreadable") : Task.FromResult(TurnRecovery.Unavailable);
        }
    }
}
