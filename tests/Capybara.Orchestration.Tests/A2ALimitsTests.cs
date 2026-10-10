using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Orchestration.Coordination;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

/// <summary>
/// Review round 1, item 5 and M5: the request/answer loop counts against the per-pair limit and survives a restart;
/// tasks past their deadline or out of budget fail; consults share the fleet-wide session slots.
/// </summary>
public sealed class A2ALimitsTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private static readonly AgentId Research = AgentId.Parse("research");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoordinatorFixture Fleet() => new(
        TestAgents.Create("ea").WithPeers("research"),
        TestAgents.Create("research").WithPeers("ea"));

    private static async Task<string> DelegateAsync(CoordinatorFixture f, TimeSpan? deadline = null)
    {
        var root = f.Envelopes.CreateRoot("user", Ea, new Budget(10m, 100, TimeSpan.FromMinutes(10)), deadline ?? TimeSpan.FromMinutes(10));
        var result = await f.Coordinator.DelegateTaskAsync(Ea, SessionIds.Main(Ea), root, Research, "find X", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        Assert.True(result.Success, result.Error);
        return result.Reference!;
    }

    [Fact]
    public async Task The_request_answer_loop_counts_against_the_pair_limit()
    {
        using var f = Fleet();
        var taskId = await DelegateAsync(f); // exchange 1

        Assert.True((await f.Coordinator.RequestInputAsync(Research, taskId, "q1", Ct)).Success); // 2
        Assert.True((await f.Coordinator.ProvideInputAsync(Ea, SessionIds.Main(Ea), taskId, "a1", Ct)).Success); // 3
        Assert.True((await f.Coordinator.RequestInputAsync(Research, taskId, "q2", Ct)).Success); // 4
        var fifth = await f.Coordinator.ProvideInputAsync(Ea, SessionIds.Main(Ea), taskId, "a2", Ct);

        Assert.False(fifth.Success);
        Assert.Contains("involve the user", fifth.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pair_count_survives_a_restart_through_the_task_record()
    {
        using var f = Fleet();
        var taskId = await DelegateAsync(f);
        await f.Coordinator.RequestInputAsync(Research, taskId, "q1", Ct);
        await f.Coordinator.ProvideInputAsync(Ea, SessionIds.Main(Ea), taskId, "a1", Ct);

        using var restarted = f.NewCoordinator();
        Assert.True((await restarted.RequestInputAsync(Research, taskId, "q2", Ct)).Success);
        var limited = await restarted.ProvideInputAsync(Ea, SessionIds.Main(Ea), taskId, "a2", Ct);

        Assert.False(limited.Success);
        Assert.Equal(4, (await f.Tasks.GetAsync(taskId, Ct))!.Exchanges);
    }

    [Fact]
    public async Task A_task_past_its_deadline_fails_and_its_requester_is_told()
    {
        using var f = Fleet();
        var taskId = await DelegateAsync(f, TimeSpan.FromMinutes(1));
        f.Time.Advance(TimeSpan.FromMinutes(2));

        var asked = await f.Coordinator.RequestInputAsync(Research, taskId, "q", Ct);

        Assert.False(asked.Success);
        var task = (await f.Tasks.GetAsync(taskId, Ct))!;
        Assert.Equal(FleetTaskState.Failed, task.State);
        Assert.Equal("its deadline passed", task.FailureReason);
        var notice = Assert.Single(await f.Inbox.ListAsync(Ea, Ct));
        Assert.StartsWith($"[task {taskId} failed]", notice.Body, StringComparison.Ordinal);
        Assert.Equal(SessionIds.Main(Ea), notice.SessionId);
    }

    [Fact]
    public async Task A_task_that_spent_its_budget_cannot_run_again()
    {
        using var f = Fleet();
        var taskId = await DelegateAsync(f);

        await f.Coordinator.RecordTaskSpendAsync(Research, taskId, 1.5m, Ct);
        Assert.Equal(2.5m, (await f.Coordinator.GetRunnableTaskAsync(Research, taskId, Ct))!.Envelope.Budget.Credits);

        await f.Coordinator.RecordTaskSpendAsync(Research, taskId, 3m, Ct);
        var task = await f.Coordinator.GetRunnableTaskAsync(Research, taskId, Ct);

        Assert.Equal(FleetTaskState.Failed, task!.State);
        Assert.Equal("its budget is exhausted", task.FailureReason);
    }

    [Fact]
    public async Task Message_ping_pong_limits_under_a_main_session_root_survive_a_restart()
    {
        using var f = Fleet();
        var root = f.Root("ea");
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await f.Coordinator.SendMessageAsync(Ea, SessionIds.Main(Ea), root, Research, $"ping {i}", Ct)).Success);
        }

        // The replies continue the same chain (root task) from research's side.
        var reply = f.Root("research") with { RootTaskId = root.RootTaskId };
        using var restarted = f.NewCoordinator();
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await restarted.SendMessageAsync(Research, SessionIds.Main(Research), reply, Ea, $"pong {i}", Ct)).Success);
        }

        var fifth = await restarted.SendMessageAsync(Ea, SessionIds.Main(Ea), root, Research, "ping again", Ct);

        Assert.False(fifth.Success);
        Assert.Contains("involve the user", fifth.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Exchange_counts_idle_past_the_retention_are_forgotten()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var ledger = new ExchangeLedger(time, TimeSpan.FromHours(1));

        Assert.Equal(1, ledger.Count("root:a|b"));
        Assert.Equal(2, ledger.Count("root:a|b"));
        time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, ledger.Count("root:a|b"));
        Assert.Equal(6, ledger.Count("root:a|b", persisted: 5));
    }

    [Fact]
    public async Task Consults_take_a_slot_from_the_shared_fleet_wide_pool()
    {
        using var f = Fleet();
        var held = Enumerable.Range(0, f.Sessions.Capacity).Select(_ => f.Sessions.TryAcquire()!).ToList();

        var refused = await f.Coordinator.AskAgentAsync(Ea, SessionIds.Main(Ea), f.Root("ea"), Research, "q", Ct);
        held[0].Dispose();
        var allowed = await f.Coordinator.AskAgentAsync(Ea, SessionIds.Main(Ea), f.Root("ea"), Research, "q", Ct);

        Assert.False(refused.Success);
        Assert.Contains("fleet-wide", refused.Error, StringComparison.Ordinal);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, f.Sessions.Available); // the finished consult gave its slot back
        held.ForEach(h => h.Dispose());
    }
}
