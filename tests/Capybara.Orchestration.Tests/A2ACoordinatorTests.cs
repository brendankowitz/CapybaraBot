using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

public sealed class A2ACoordinatorTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private static readonly AgentId Research = AgentId.Parse("research");
    private static readonly AgentId Code = AgentId.Parse("code");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoordinatorFixture Fleet() => new(
        TestAgents.Create("ea").WithPeers("research", "code"),
        TestAgents.Create("research").WithPeers("code"),
        TestAgents.Create("code").WithPeers("ea", "research"));

    [Fact]
    public async Task Consult_returns_the_answer_with_a_child_envelope()
    {
        using var f = Fleet();

        var result = await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", f.Root("ea"), Research, "weather?", Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal("42", result.Content);
        var (target, envelope) = Assert.Single(f.Executor.Calls);
        Assert.Equal(Research, target);
        Assert.Equal(1, envelope.Depth);
        Assert.Equal([Ea, Research], envelope.HopPath);
        Assert.Single(f.Thread.Entries);
    }

    [Fact]
    public async Task Peer_allowlist_is_enforced()
    {
        using var f = Fleet();

        var result = await f.Coordinator.AskAgentAsync(Research, "agent_research_main", f.Root("research"), Ea, "hi", Ct);

        Assert.False(result.Success);
        Assert.Empty(f.Executor.Calls);
        Assert.True(Assert.Single(f.Thread.Entries).Rejected);
    }

    [Fact]
    public async Task Tainted_consult_taints_the_caller()
    {
        using var f = Fleet();
        f.Executor.ReplyTaint = Taint.Tainted;

        var result = await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", f.Root("ea"), Research, "news?", Ct);

        Assert.Equal(Taint.Tainted, result.Taint);
        Assert.True(f.Taint.IsTainted("agent_ea_main"));
    }

    [Fact]
    public async Task Depth_and_cycles_are_enforced()
    {
        using var f = Fleet();
        var root = f.Root("ea");
        var depth1 = f.Envelopes.CreateChild(root, Code, new Budget(5m, 50, TimeSpan.FromMinutes(5)), Taint.Clean).Child!;
        var depth2 = f.Envelopes.CreateChild(depth1, Research, new Budget(2m, 20, TimeSpan.FromMinutes(2)), Taint.Clean).Child!;

        Assert.False((await f.Coordinator.AskAgentAsync(Research, "s", depth2, Code, "deeper?", Ct)).Success);
        Assert.False((await f.Coordinator.AskAgentAsync(Code, "s", depth1, Ea, "back to you", Ct)).Success);
    }

    [Fact]
    public async Task Ping_pong_between_a_pair_is_capped()
    {
        using var f = Fleet();
        var root = f.Root("ea");

        var results = new List<bool>();
        for (var i = 0; i < 6; i++)
        {
            results.Add((await f.Coordinator.SendMessageAsync(Ea, "agent_ea_main", root, Code, $"msg {i}", Ct)).Success);
        }

        Assert.Equal(4, results.Count(r => r));
    }

    [Fact]
    public async Task Paused_agents_cannot_be_reached()
    {
        using var f = Fleet();
        f.KillSwitch.Pause(Research);

        Assert.False((await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", f.Root("ea"), Research, "hi", Ct)).Success);
    }

    [Fact]
    public async Task Delegated_tasks_bubble_input_requests_and_only_the_owner_completes()
    {
        using var f = Fleet();
        var delegated = await f.Coordinator.DelegateTaskAsync(Ea, "agent_ea_main", f.Root("ea"), Code, "fix the bug", null, [], new Budget(2m, 20, TimeSpan.FromMinutes(5)), Ct);
        Assert.True(delegated.Success, delegated.Error);
        var taskId = delegated.Reference!;

        Assert.True((await f.Coordinator.RequestInputAsync(Code, taskId, "which repo?", Ct)).Success);
        Assert.Equal(FleetTaskState.InputRequired, (await f.Tasks.GetAsync(taskId, Ct))!.State);
        Assert.Equal(1, await f.Inbox.CountAsync(Ea, Ct));

        Assert.False((await f.Coordinator.CompleteTaskAsync(Ea, taskId, true, [], [], Ct)).Success);
        Assert.False((await f.Coordinator.CompleteTaskAsync(Code, taskId, verifiedAgainstObjective: false, [], [], Ct)).Success);
        Assert.True((await f.Coordinator.CompleteTaskAsync(Code, taskId, true, [], [], Ct)).Success);
        Assert.Equal(FleetTaskState.Completed, (await f.Tasks.GetAsync(taskId, Ct))!.State);
    }

    [Fact]
    public async Task Task_store_rejects_path_like_ids()
    {
        using var f = Fleet();
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await f.Tasks.GetAsync("../../etc/passwd", Ct));
    }
}
