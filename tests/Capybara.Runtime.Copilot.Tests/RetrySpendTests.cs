using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 2, N5: every attempt's spend comes out of the carved budget (FR-A2A-05), so retries of a failing task
/// turn or chained message cannot spend it many times over.
/// </summary>
public sealed class RetrySpendTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea").WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static readonly InboxPumpOptions Fast = new()
    {
        MaxAttempts = 8,
        InitialRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxRetryDelay = TimeSpan.FromMilliseconds(10),
        BlockedPollInterval = TimeSpan.FromMilliseconds(10),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TurnResult FailedSpending(decimal credits) => TurnResult.Failed("model error", new TurnEvidence(credits, "auto", [], [], [], []));

    private static async Task RunUntilAsync(InboxPump pump, Func<Task<bool>> done, string what)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = pump.RunAsync(stop.Token);
        try
        {
            await Eventually.TrueAsync(done, what);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Failed_task_turns_are_debited_until_the_task_runs_out_and_fails()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        var runtime = new FakeAgentRuntime(Research.Id) { Turn = (_, _) => Task.FromResult(FailedSpending(1.5m)) };

        // The message leaves the inbox only once the task failed. Only the file count is polled: reading the task file
        // while the pump replaces it makes the replace fail on Windows.
        await RunUntilAsync(
            new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance),
            async () => await f.Inbox.CountAsync(Research.Id, Ct) == 0,
            "task failed once its budget was spent");

        // 4 credits: attempts at 4, 2.5 and 1 remaining; then the task is out of budget and gets no more turns.
        Assert.Equal(3, runtime.Requests.Count);
        var task = (await f.Tasks.GetAsync(taskId, Ct))!;
        Assert.Equal((FleetTaskState.Failed, "its budget is exhausted"), (task.State, task.FailureReason));
    }

    [Fact]
    public async Task Retries_of_a_chained_message_run_on_what_failed_attempts_left()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var root = f.Envelopes.CreateRoot("user", Research.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10), rootOrigin: MessageOrigin.User);
        await f.Coordinator.SendMessageAsync(Research.Id, SessionIds.Main(Research.Id), root, Ea.Id, "please look", Ct);
        var runtime = new FakeAgentRuntime(Ea.Id) { Turn = (_, _) => Task.FromResult(FailedSpending(0.8m)) };

        await RunUntilAsync(
            new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance),
            async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0,
            "dropped once its budget was spent");

        Assert.Equal([2m, 1.2m, 0.4m], runtime.Requests.Select(r => r.Envelope!.Budget.Credits));
        Assert.DoesNotContain(f.Alerts.Recent, a => a.Kind == InboxPump.DeadLetterAlertKind);
    }

    [Fact]
    public async Task A_chained_message_that_arrives_with_nothing_left_still_gets_its_turn()
    {
        // Review round 3, I1: only this pump's own failed attempts may use a message up.
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var spent = f.Envelopes.CreateRoot("user", Research.Id, new Budget(0m, 10, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10), rootOrigin: MessageOrigin.User) with { Depth = 1 };
        await f.Inbox.EnqueueAsync(new FleetMessage { To = Ea.Id, From = Research.Id, Origin = MessageOrigin.Agent, Body = "fyi", Envelope = spent, CreatedAt = f.Time.GetUtcNow() }, Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);

        await RunUntilAsync(
            new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance),
            async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0,
            "handled");

        Assert.Equal(0m, Assert.Single(runtime.Requests).Envelope!.Budget.Credits);
    }

    [Fact]
    public async Task A_handled_task_turn_whose_debit_fails_is_acknowledged_once_and_alerted()
    {
        // Review round 3, I3: a task-store fault while debiting used to fail a completed turn, which then ran again.
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var store = new FaultyTaskStore(f.Tasks);
        using var coordinator = new A2ACoordinator(f.Registry, f.Envelopes, f.Inbox, store, new FleetThread(), f.Taint, f.KillSwitch, f.Audit, f.Consults, new A2AOptions(), f.Time, f.Sessions);
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        var runtime = new FakeAgentRuntime(Research.Id)
        {
            Turn = (_, _) =>
            {
                store.FailSaves = true;
                return Task.FromResult(TurnResult.Completed("done", new TurnEvidence(1m, "auto", [], [], [], [])));
            },
        };

        await RunUntilAsync(
            new InboxPump(runtime, f.Inbox, coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance),
            async () => await f.Inbox.CountAsync(Research.Id, Ct) == 0 && f.Alerts.Recent.Any(a => a.Kind == InboxPump.SpendUnrecordedAlertKind),
            "acknowledged and alerted");
        await Task.Delay(100, Ct);

        Assert.Single(runtime.Requests);
        Assert.Contains(taskId, Assert.Single(f.Alerts.Recent, a => a.Kind == InboxPump.SpendUnrecordedAlertKind).Message, StringComparison.Ordinal);
    }

    private sealed class FaultyTaskStore(ITaskStore inner) : ITaskStore
    {
        public volatile bool FailSaves;

        public ValueTask SaveAsync(FleetTask task, CancellationToken cancellationToken = default) =>
            FailSaves ? throw new IOException("disk full") : inner.SaveAsync(task, cancellationToken);

        public ValueTask<FleetTask?> GetAsync(string taskId, CancellationToken cancellationToken = default) => inner.GetAsync(taskId, cancellationToken);

        public IAsyncEnumerable<FleetTask> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    }
}
