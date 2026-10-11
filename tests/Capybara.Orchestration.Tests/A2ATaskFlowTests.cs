using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

/// <summary>
/// FR-A2A-04/05/07/09: envelope debit across sequential calls, delegated-task routing back to the delegating session,
/// the INPUT_REQUIRED round trip and completion.
/// </summary>
public sealed class A2ATaskFlowTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private static readonly AgentId Research = AgentId.Parse("research");
    private static readonly AgentId Code = AgentId.Parse("code");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoordinatorFixture Fleet() => new(
        TestAgents.Create("ea").WithPeers("research", "code"),
        TestAgents.Create("research").WithPeers("code"),
        TestAgents.Create("code").WithPeers("ea", "research"));

    private static Envelope RootWith(CoordinatorFixture f, decimal credits) =>
        f.Envelopes.CreateRoot("user", Ea, new Budget(credits, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));

    [Fact]
    public async Task Sequential_consults_debit_the_parent_and_the_second_fails_once_the_budget_is_spent()
    {
        using var f = Fleet();
        var root = RootWith(f, 3m); // the default consult budget is 2 credits

        var first = await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", root, Research, "q1", Ct);
        Assert.True(first.Success, first.Error);
        Assert.Equal(1m, first.ParentRemainder!.Budget.Credits);

        var second = await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", first.ParentRemainder, Code, "q2", Ct);

        Assert.False(second.Success);
        Assert.Contains("budget", second.Error, StringComparison.Ordinal);
        Assert.Null(second.ParentRemainder);
        Assert.Single(f.Executor.Calls);
    }

    [Fact]
    public async Task A_failed_consult_still_spends_its_budget()
    {
        using var f = Fleet();
        f.Executor.Failure = new InvalidOperationException("boom");

        var result = await f.Coordinator.AskAgentAsync(Ea, "agent_ea_main", RootWith(f, 5m), Research, "q", Ct);

        Assert.False(result.Success);
        Assert.Equal(3m, result.ParentRemainder!.Budget.Credits);
    }

    [Fact]
    public async Task Messages_debit_the_parent_too()
    {
        using var f = Fleet();

        var result = await f.Coordinator.SendMessageAsync(Ea, "agent_ea_main", RootWith(f, 5m), Research, "fyi", Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3m, result.ParentRemainder!.Budget.Credits);
    }

    [Fact]
    public async Task Delegated_task_runs_in_a_task_session_and_replies_to_the_delegating_session()
    {
        using var f = Fleet();
        var result = await f.Coordinator.DelegateTaskAsync(Ea, "agent_ea_main", RootWith(f, 10m), Research, "find X", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        Assert.True(result.Success, result.Error);
        Assert.Equal(6m, result.ParentRemainder!.Budget.Credits);

        var task = await f.Tasks.GetAsync(result.Reference!, Ct);
        Assert.Equal("agent_ea_main", task!.RequesterSessionId);

        var assignment = Assert.Single(await f.Inbox.ListAsync(Research, Ct));
        Assert.Equal(task.Id, assignment.TaskId);
        Assert.Equal(SessionIds.Task(Research, task.Id), assignment.SessionId);
    }

    [Fact]
    public async Task Delegating_from_a_task_session_records_the_parent_task_and_persists_the_debit()
    {
        using var f = Fleet();
        var outer = await f.Coordinator.DelegateTaskAsync(Ea, "agent_ea_main", RootWith(f, 10m), Research, "outer", null, [], new Budget(6m, 20, TimeSpan.FromMinutes(5)), Ct);
        var outerTask = (await f.Tasks.GetAsync(outer.Reference!, Ct))!;
        var researchSession = SessionIds.Task(Research, outerTask.Id);

        var inner = await f.Coordinator.DelegateTaskAsync(Research, researchSession, outerTask.Envelope, Code, "inner", null, [], new Budget(2m, 5, TimeSpan.FromMinutes(1)), Ct);

        Assert.True(inner.Success, inner.Error);
        var innerTask = (await f.Tasks.GetAsync(inner.Reference!, Ct))!;
        Assert.Equal(outerTask.Id, innerTask.Envelope.ParentTaskId);
        Assert.Equal(researchSession, innerTask.RequesterSessionId);
        Assert.Equal(4m, (await f.Tasks.GetAsync(outerTask.Id, Ct))!.Envelope.Budget.Credits);
    }

    [Fact]
    public async Task Input_required_bubbles_to_the_requester_session_and_the_answer_returns_to_the_task_session()
    {
        using var f = Fleet();
        var delegated = await f.Coordinator.DelegateTaskAsync(Ea, "agent_ea_main", RootWith(f, 10m), Research, "find X", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        var taskId = delegated.Reference!;
        var researchSession = SessionIds.Task(Research, taskId);
        f.Taint.MarkTainted(researchSession);

        var asked = await f.Coordinator.RequestInputAsync(Research, researchSession, taskId, "which X?", Ct);

        Assert.True(asked.Success, asked.Error);
        Assert.Equal(FleetTaskState.InputRequired, (await f.Tasks.GetAsync(taskId, Ct))!.State);
        var question = Assert.Single(await f.Inbox.ListAsync(Ea, Ct));
        Assert.Equal("agent_ea_main", question.SessionId);
        Assert.Equal(MessageOrigin.Agent, question.Origin);
        Assert.Equal(Taint.Tainted, question.Taint);

        Assert.False((await f.Coordinator.ProvideInputAsync(Code, "agent_code_main", taskId, "the red one", Ct)).Success);
        var answered = await f.Coordinator.ProvideInputAsync(Ea, "agent_ea_main", taskId, "the red one", Ct);

        Assert.True(answered.Success, answered.Error);
        Assert.Equal(FleetTaskState.Working, (await f.Tasks.GetAsync(taskId, Ct))!.State);
        var answer = (await f.Inbox.ListAsync(Research, Ct))[^1];
        Assert.Equal(researchSession, answer.SessionId);
        Assert.Contains("the red one", answer.Body, StringComparison.Ordinal);
        Assert.False((await f.Coordinator.ProvideInputAsync(Ea, "agent_ea_main", taskId, "again", Ct)).Success);
    }

    [Fact]
    public async Task Completion_carries_the_owner_taint_back_to_the_requester_session()
    {
        using var f = Fleet();
        var delegated = await f.Coordinator.DelegateTaskAsync(Ea, "agent_ea_main", RootWith(f, 10m), Research, "find X", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        var taskId = delegated.Reference!;

        var done = await f.Coordinator.CompleteTaskAsync(Research, taskId, true, [new Artifact("result.md", "artifacts/x.md")], ["source date"], Taint.Tainted, Ct);

        Assert.True(done.Success, done.Error);
        var message = Assert.Single(await f.Inbox.ListAsync(Ea, Ct));
        Assert.Equal(taskId, message.TaskId);
        Assert.Equal("agent_ea_main", message.SessionId);
        Assert.Equal(Taint.Tainted, message.Taint);
        Assert.Single(message.Attachments);
        Assert.False((await f.Coordinator.CompleteTaskAsync(Research, taskId, true, [], [], Ct)).Success);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("not-a-task")]
    public async Task Model_supplied_ids_that_are_not_task_ids_are_rejected_not_thrown(string taskId)
    {
        using var f = Fleet();

        Assert.False((await f.Coordinator.ProvideInputAsync(Ea, "agent_ea_main", taskId, "x", Ct)).Success);
        Assert.False((await f.Coordinator.RequestInputAsync(Ea, taskId, "x", Ct)).Success);
        Assert.Null(await f.Coordinator.GetOwnedTaskAsync(Ea, taskId, Ct));
    }

    [Fact]
    public async Task Dead_lettered_messages_leave_the_queue_but_stay_on_disk()
    {
        using var f = Fleet();
        await f.Coordinator.SendMessageAsync(Ea, "agent_ea_main", RootWith(f, 10m), Research, "one", Ct);
        var message = Assert.Single(await f.Inbox.ListAsync(Research, Ct));

        await f.Inbox.DeadLetterAsync(Research, message.Id, Ct);

        Assert.Empty(await f.Inbox.ListAsync(Research, Ct));
        Assert.Null(await f.Inbox.PeekAsync(Research, Ct));
    }
}
