using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Lifecycle;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 1, items 2 and 7: a stopped turn is aborted (and its idle awaited) before the failure surfaces; a turn
/// that passes its own credit limit trips the abort; routine credits reach the ledger live and exactly once.
/// </summary>
public sealed class TurnAbortAndBudgetTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessageOptions Hello = new() { Prompt = "hello" };

    [Fact]
    public async Task A_completed_send_is_not_aborted()
    {
        var session = new FakeTurnSession { Reply = "hi" };

        Assert.Equal("hi", await TurnSender.SendAsync(session, Hello, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), Ct));
        Assert.Equal(["send"], session.Calls);
    }

    [Fact]
    public async Task A_timed_out_send_is_aborted_and_idle_awaited_before_the_failure_surfaces()
    {
        var session = new FakeTurnSession { SendFailure = new TimeoutException("too slow"), AbortDelay = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<TimeoutException>(() => TurnSender.SendAsync(session, Hello, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), Ct));

        Assert.Equal(["send", "abort", "idle"], session.Calls);
    }

    [Fact]
    public async Task A_cancelled_send_is_aborted_too()
    {
        using var cancel = new CancellationTokenSource();
        var session = new FakeTurnSession { WaitForCancellation = true };

        var send = TurnSender.SendAsync(session, Hello, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), cancel.Token);
        await Eventually.TrueAsync(() => session.Calls.Contains("send"), "send started");
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(["send", "abort", "idle"], session.Calls);
    }

    [Fact]
    public async Task An_abort_that_fails_tells_the_caller_to_discard_the_session()
    {
        var session = new FakeTurnSession { SendFailure = new TimeoutException("too slow"), AbortFailure = new TimeoutException("never idle") };

        var ex = await Assert.ThrowsAsync<TurnAbortFailedException>(() => TurnSender.SendAsync(session, Hello, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), Ct));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal("never idle", ex.AbortFailure!.Message);
    }

    [Fact]
    public void A_turn_that_passes_its_credit_limit_fires_its_limit_callbacks_once()
    {
        var turn = new SessionTurnLog(creditLimit: 1m);
        var fired = 0;
        using var hook = turn.OnLimitExceeded(() => fired++);

        turn.RecordUsage("auto", 0.6m);
        Assert.Equal(0, fired);
        turn.RecordUsage("auto", 0.6m);
        turn.RecordUsage("auto", 0.6m);

        Assert.Equal(1, fired);
        Assert.True(turn.LimitExceeded);

        var late = 0;
        using var lateHook = turn.OnLimitExceeded(() => late++);
        Assert.Equal(1, late);
    }

    [Fact]
    public void A_turn_without_a_limit_never_fires()
    {
        var turn = new SessionTurnLog();
        var fired = false;
        using var hook = turn.OnLimitExceeded(() => fired = true);

        turn.RecordUsage("auto", 1_000m);

        Assert.False(fired);
    }

    [Fact]
    public async Task Routine_usage_reaches_the_ledger_live_attributed_to_the_routine()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea);
        session.RoutineId = "morning-brief";
        session.BeginTurn(creditLimit: 5m);

        f.Usage.OnEvent(session, Usage(2));

        await Eventually.TrueAsync(() => f.Ledger.Records.Count == 1, "recorded during the run");
        Assert.Equal("morning-brief", Assert.Single(f.Ledger.Records).RoutineId);
        Assert.Equal(2m, session.Turn.Snapshot().Credits);
    }

    [Fact]
    public async Task Routine_credits_are_in_the_ledger_exactly_once_through_the_real_dispatcher()
    {
        using var f = new RuntimeFixture();
        await using var killSwitch = new AsyncKillSwitch();
        var runtime = new FakeAgentRuntime(Ea.Id)
        {
            Turn = (request, _) =>
            {
                // What the SDK's usage events do during a real turn.
                var session = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, f.Session(Ea).Envelope) { RoutineId = request.RoutineId };
                var turn = session.BeginTurn(request.Budget?.Credits);
                f.Usage.OnEvent(session, Usage(0.75));
                f.Usage.OnEvent(session, Usage(0.5));
                return Task.FromResult(TurnResult.Completed("Brief ready.", turn.Snapshot()));
            },
        };
        await using var pool = new AgentRuntimePool(new AgentRegistry([Ea], killSwitch.Value), _ => runtime);
        var executor = new CopilotRoutineExecutor(pool, f.Options, NullLogger<CopilotRoutineExecutor>.Instance);
        var registry = new RoutineRegistry(TimeZoneInfo.Utc);
        var routine = new RoutineSpec { Id = "brief", Owner = Ea.Id, Kind = TriggerKind.Cron, Cron = "0 0 7 * * ?", Prompt = "Brief me." };
        registry.AddOrUpdate(routine, RoutinePrincipal.Human("user"));
        registry.Enable(routine.Id, registry.ProjectedMonthlyCredits(routine), RoutinePrincipal.Human("user"));
        using var dispatcher = new RoutineDispatcher(executor, new RoutineRunHistory(f.Alerts), f.Alerts, f.Ledger, registry, killSwitch.Value, TimeProvider.System);

        var result = await dispatcher.DispatchAsync(new RoutineFiring(routine, DateTimeOffset.UtcNow, "run-1"), Ct);

        Assert.Equal(RoutineExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1.25m, result.CreditsUsed);
        await Eventually.TrueAsync(() => f.Ledger.Records.Count >= 2, "live usage recorded");
        await Task.Delay(50, Ct);
        Assert.Equal(1.25m, f.Ledger.Records.Sum(r => r.Credits));
        Assert.All(f.Ledger.Records, r => Assert.Equal("brief", r.RoutineId));
    }

    [Theory]
    [InlineData(TurnStatus.LimitExceeded, RoutineExecutionStatus.OverBudget)]
    [InlineData(TurnStatus.Throttled, RoutineExecutionStatus.Skipped)]
    public async Task Stopped_and_throttled_runs_map_to_routine_statuses(TurnStatus turn, RoutineExecutionStatus expected)
    {
        await using var killSwitch = new AsyncKillSwitch();
        var runtime = new FakeAgentRuntime(Ea.Id) { Turn = (_, _) => Task.FromResult(new TurnResult(turn, null, "stopped", TurnEvidence.Empty)) };
        await using var pool = new AgentRuntimePool(new AgentRegistry([Ea], killSwitch.Value), _ => runtime);
        var executor = new CopilotRoutineExecutor(pool, new CopilotRuntimeOptions(), NullLogger<CopilotRoutineExecutor>.Instance);
        var routine = new RoutineSpec { Id = "r", Owner = Ea.Id, Kind = TriggerKind.Cron, Prompt = "x" };

        var result = await executor.ExecuteAsync(new RoutineInvocation(routine, RoutineTriggerKind.Cron, DateTimeOffset.UtcNow, "run", false, null, "auto", SessionTarget.IsolatedTask), Ct);

        Assert.Equal(expected, result.Status);
        Assert.True(result.CreditsLedgered);
    }

    private static AssistantUsageEvent Usage(double cost) => new() { Data = new AssistantUsageData { Model = "auto", Cost = cost } };

    /// <summary>Records the order of send, abort and idle.</summary>
    private sealed class FakeTurnSession : ITurnSession
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];

        public string? Reply { get; init; }

        public Exception? SendFailure { get; init; }

        public bool WaitForCancellation { get; init; }

        public Exception? AbortFailure { get; init; }

        public TimeSpan AbortDelay { get; init; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public async Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Add("send");
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return SendFailure is null ? Reply : throw SendFailure;
        }

        public async Task AbortAsync(TimeSpan idleTimeout)
        {
            Add("abort");
            await Task.Delay(AbortDelay);
            if (AbortFailure is not null)
            {
                throw AbortFailure;
            }

            Add("idle");
        }

        private void Add(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }

    private sealed class AsyncKillSwitch : IAsyncDisposable
    {
        public KillSwitch Value { get; } = new(TimeProvider.System);

        public ValueTask DisposeAsync()
        {
            Value.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
