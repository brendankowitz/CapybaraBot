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

        await RunUntilAsync(
            new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance),
            async () => (await f.Tasks.GetAsync(taskId, Ct))!.State == FleetTaskState.Failed && await f.Inbox.CountAsync(Research.Id, Ct) == 0,
            "task failed once its budget was spent");

        // 4 credits: attempts at 4, 2.5 and 1 remaining; then the task is out of budget and gets no more turns.
        Assert.Equal(3, runtime.Requests.Count);
        Assert.Equal("its budget is exhausted", (await f.Tasks.GetAsync(taskId, Ct))!.FailureReason);
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
}
