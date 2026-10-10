using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 1, item 5 and M5 on the runtime side: non-main turns take a fleet-wide slot; throttled and expired work
/// is handled without retries or turns; task spend is debited; per-session locks do not accumulate.
/// </summary>
public sealed class FleetLimitsRuntimeTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea").WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_non_main_turn_without_a_free_fleet_slot_is_throttled_before_touching_the_cli()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        await using var runtime = f.Runtime(Ea);
        var held = Enumerable.Range(0, f.Sessions.Capacity).Select(_ => f.Sessions.TryAcquire()!).ToList();

        var result = await runtime.RunTurnAsync(new AgentTurnRequest { Prompt = "cron", Origin = MessageOrigin.System, Principal = "routine:r", Target = TurnTarget.Isolated("routine-1") }, Ct);

        Assert.Equal(TurnStatus.Throttled, result.Status);
        held.ForEach(h => h.Dispose());
    }

    [Fact]
    public async Task Throttled_turns_are_deferred_without_counting_toward_the_dead_letter_limit()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var calls = 0;
        var runtime = new FakeAgentRuntime(Ea.Id)
        {
            Turn = (_, _) => Task.FromResult(++calls <= 6 ? TurnResult.Throttled("busy") : TurnResult.Completed("ok", TurnEvidence.Empty)),
        };
        await f.Inbox.EnqueueAsync(new FleetMessage { To = Ea.Id, From = Research.Id, Origin = MessageOrigin.Agent, Body = "hi" }, Ct);
        var options = new InboxPumpOptions { MaxAttempts = 2, InitialRetryDelay = TimeSpan.FromMilliseconds(5), BlockedPollInterval = TimeSpan.FromMilliseconds(5) };
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, options, TimeProvider.System, NullLogger<InboxPump>.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = pump.RunAsync(stop.Token);
        await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "processed once a slot was free");
        await stop.CancelAsync();
        await run;

        Assert.Equal(7, calls);
        Assert.Empty(f.Alerts.Recent);
    }

    [Fact]
    public async Task A_task_turn_debits_the_task_budget_and_an_expired_task_gets_no_turn()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        var runtime = new FakeAgentRuntime(Research.Id) { Turn = (_, _) => Task.FromResult(TurnResult.Completed("working on it", new TurnEvidence(1.5m, "auto", [], [], [], []))) };
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, new InboxPumpOptions(), TimeProvider.System, NullLogger<InboxPump>.Instance);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = pump.RunAsync(stop.Token);
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Research.Id, Ct) == 0, "assignment processed");
            await stop.CancelAsync();
            await run;
        }

        var task = (await f.Tasks.GetAsync(taskId, Ct))!;
        Assert.Equal(2.5m, task.Envelope.Budget.Credits);
        Assert.Equal(FleetTaskState.Working, task.State);

        // Past the deadline, the answer to a question is not run: the task fails instead.
        await f.Inbox.EnqueueAsync(new FleetMessage { To = Research.Id, From = Ea.Id, Origin = MessageOrigin.Agent, Body = "late", TaskId = taskId, SessionId = SessionIds.Task(Research.Id, taskId) }, Ct);
        f.Time.Advance(TimeSpan.FromMinutes(30));
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = pump.RunAsync(stop.Token);
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Research.Id, Ct) == 0, "late message dropped");
            await stop.CancelAsync();
            await run;
        }

        Assert.Single(runtime.Requests);
        Assert.Equal(FleetTaskState.Failed, (await f.Tasks.GetAsync(taskId, Ct))!.State);
    }

    [Fact]
    public async Task Per_session_locks_serialize_holders_and_are_removed_when_free()
    {
        var locks = new KeyedLocks();
        var first = await locks.AcquireAsync("agent:ea:task:a", Ct);
        var second = locks.AcquireAsync("agent:ea:task:a", Ct);
        var other = await locks.AcquireAsync("agent:ea:task:b", Ct);

        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
        other.Dispose();

        Assert.Equal(0, locks.Count);
    }
}
