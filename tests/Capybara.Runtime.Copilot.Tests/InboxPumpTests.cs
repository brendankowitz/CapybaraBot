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
/// FR-AGT-05 / NFR-02: one consumer per agent, FIFO, acknowledged only after the turn persists, user DMs pre-empt by
/// immediate steering, and delegated tasks run in task sessions with results bubbling back to the requester.
/// </summary>
public sealed class InboxPumpTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea").WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly InboxPumpOptions FastRetries = new()
    {
        MaxAttempts = 3,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(20),
        BlockedPollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static RuntimeFixture Fleet() => new(fleet: [Ea, Research]);

    private static InboxPump Pump(RuntimeFixture f, FakeAgentRuntime runtime, InboxPumpOptions? options = null) =>
        new(runtime, f.Inbox, f.Coordinator, f.Alerts, options ?? FastRetries, TimeProvider.System, NullLogger<InboxPump>.Instance);

    private static FleetMessage Message(AgentId to, string body, MessageOrigin origin = MessageOrigin.Agent) => new()
    {
        To = to,
        From = origin == MessageOrigin.Agent ? Research.Id : null,
        Origin = origin,
        Body = body,
    };

    private static async Task<T> RunningAsync<T>(InboxPump pump, Func<Task<T>> body)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = pump.RunAsync(stop.Token);
        try
        {
            return await body();
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Messages_run_in_order_and_each_is_acknowledged_only_after_its_turn()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id);
        var pendingDuringTurns = new List<int>();
        runtime.Turn = async (_, _) =>
        {
            pendingDuringTurns.Add(await f.Inbox.CountAsync(Ea.Id, Ct));
            return TurnResult.Completed("ok", TurnEvidence.Empty);
        };
        foreach (var body in new[] { "one", "two", "three" })
        {
            await f.Inbox.EnqueueAsync(Message(Ea.Id, body), Ct);
        }

        await RunningAsync(Pump(f, runtime), async () =>
        {
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "inbox drained");
            return 0;
        });

        Assert.Equal(["one", "two", "three"], runtime.Requests.Select(r => BodyOf(r.Prompt)));
        Assert.Equal([3, 2, 1], pendingDuringTurns);
    }

    [Fact]
    public async Task A_failed_turn_keeps_the_message_and_retries_it()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id);
        var calls = 0;
        runtime.Turn = (_, _) => Task.FromResult(++calls == 1 ? TurnResult.Failed("boom") : TurnResult.Completed("ok", TurnEvidence.Empty));
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "flaky"), Ct);

        await RunningAsync(Pump(f, runtime), async () =>
        {
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "acknowledged after retry");
            return 0;
        });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_and_the_user_alerted()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id) { Turn = (_, _) => Task.FromResult(TurnResult.Failed("poison")) };
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "poison"), Ct);
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "behind it"), Ct);

        await RunningAsync(Pump(f, runtime), async () =>
        {
            await Eventually.TrueAsync(() => f.Alerts.Recent.Any(a => a.Kind == InboxPump.DeadLetterAlertKind), "dead-letter alert");
            return 0;
        });

        Assert.Equal(FastRetries.MaxAttempts, runtime.Requests.Count(r => BodyOf(r.Prompt) == "poison"));
        Assert.DoesNotContain("poison", f.Alerts.Recent.Single(a => a.Kind == InboxPump.DeadLetterAlertKind).Message, StringComparison.Ordinal);
        Assert.Contains(runtime.Requests, r => BodyOf(r.Prompt) == "behind it");
    }

    [Fact]
    public async Task A_paused_agent_keeps_its_messages_until_it_can_run()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id);
        var blocked = true;
        runtime.Turn = (_, _) => Task.FromResult(Volatile.Read(ref blocked) ? TurnResult.Blocked("paused") : TurnResult.Completed("ok", TurnEvidence.Empty));
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "wait"), Ct);

        await RunningAsync(Pump(f, runtime), async () =>
        {
            await Eventually.TrueAsync(() => runtime.Requests.Count >= 3, "polled while paused");
            Assert.Equal(1, await f.Inbox.CountAsync(Ea.Id, Ct));
            Volatile.Write(ref blocked, false);
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "ran once released");
            return 0;
        });

        Assert.Empty(f.Alerts.Recent);
    }

    [Fact]
    public async Task A_user_dm_is_taken_before_queued_background_work()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id);
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "background 1"), Ct);
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "background 2"), Ct);
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "hi from the user", MessageOrigin.User), Ct);

        await RunningAsync(Pump(f, runtime), async () =>
        {
            await Eventually.TrueAsync(() => runtime.Requests.Count == 3, "all ran");
            return 0;
        });

        var first = runtime.Requests.First();
        Assert.Equal("hi from the user", first.Prompt);
        Assert.Equal(MessageOrigin.User, first.Origin);
        Assert.Equal(["background 1", "background 2"], runtime.Requests.Skip(1).Select(r => BodyOf(r.Prompt)));
    }

    [Fact]
    public async Task A_user_dm_during_a_background_main_turn_is_steered_into_it_and_acknowledged_with_it()
    {
        using var f = Fleet();
        var runtime = new FakeAgentRuntime(Ea.Id);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Turn = async (_, _) =>
        {
            await release.Task;
            return TurnResult.Completed("ok", TurnEvidence.Empty);
        };
        var pump = Pump(f, runtime);
        InboxTurnCompletedEventArgs? completed = null;
        pump.TurnCompleted += (_, e) => completed = e;
        await f.Inbox.EnqueueAsync(Message(Ea.Id, "heartbeat-ish background work"), Ct);

        await RunningAsync(pump, async () =>
        {
            await Eventually.TrueAsync(() => runtime.Requests.Count == 1, "background turn started");
            await f.Inbox.EnqueueAsync(Message(Ea.Id, "stop, call me", MessageOrigin.User), Ct);
            await Eventually.TrueAsync(() => !runtime.Steered.IsEmpty, "DM steered");
            release.SetResult();
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "both acknowledged");
            return 0;
        });

        Assert.Equal(["stop, call me"], runtime.Steered);
        Assert.Single(runtime.Requests);
        Assert.Equal("stop, call me", Assert.Single(completed!.Steered).Body);
    }

    [Fact]
    public async Task A_delegated_task_runs_in_its_task_session_and_its_result_bubbles_back_to_the_requester()
    {
        using var f = Fleet();
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var delegated = await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "find the colour", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        var taskId = delegated.Reference!;

        // The owner's turn completes the task, as the complete_task tool would.
        var research = new FakeAgentRuntime(Research.Id)
        {
            Turn = async (request, ct) =>
            {
                await f.Coordinator.CompleteTaskAsync(Research.Id, taskId, true, [new Artifact("result.md", $"artifacts/tasks/{taskId}/result.md")], [], ct);
                return TurnResult.Completed("done", TurnEvidence.Empty);
            },
        };
        await RunningAsync(Pump(f, research), async () =>
        {
            await Eventually.TrueAsync(() => !research.Ended.IsEmpty, "finished task session ended");
            return 0;
        });

        var assignment = Assert.Single(research.Requests);
        Assert.Equal(SessionIds.Task(Research.Id, taskId), assignment.Target.SessionIdFor(Research.Id));
        Assert.Contains("<task id=\"" + taskId + "\" from=\"ea\"", assignment.Prompt, StringComparison.Ordinal);
        Assert.Contains("Objective: find the colour", assignment.Prompt, StringComparison.Ordinal);
        Assert.Equal(MessageOrigin.Agent, assignment.Origin);
        Assert.Equal(FleetTaskState.Completed, (await f.Tasks.GetAsync(taskId, Ct))!.State);

        var ea = new FakeAgentRuntime(Ea.Id);
        await RunningAsync(Pump(f, ea), async () =>
        {
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "requester processed the result");
            return 0;
        });

        var result = Assert.Single(ea.Requests);
        Assert.Equal(TurnTarget.Main, result.Target);
        Assert.Equal(Research.Id, result.From);
        Assert.Contains("<agent-message from=\"research\"", result.Prompt, StringComparison.Ordinal);
        Assert.Contains($"artifacts/tasks/{taskId}/result.md", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_message_about_a_finished_task_is_acknowledged_without_a_turn()
    {
        using var f = Fleet();
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        await f.Coordinator.CompleteTaskAsync(Research.Id, taskId, true, [], [], Ct);
        var research = new FakeAgentRuntime(Research.Id);

        await RunningAsync(Pump(f, research), async () =>
        {
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Research.Id, Ct) == 0, "stale assignment dropped");
            return 0;
        });

        Assert.Empty(research.Requests);
    }

    [Fact]
    public async Task Agent_messages_continue_the_conversation_root_so_exchange_limits_span_turns()
    {
        using var f = Fleet();
        var root = f.Envelopes.CreateRoot("user:brendan", Research.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        await f.Coordinator.SendMessageAsync(Research.Id, SessionIds.Main(Research.Id), root, Ea.Id, "fyi", Ct);
        var ea = new FakeAgentRuntime(Ea.Id);

        await RunningAsync(Pump(f, ea), async () =>
        {
            await Eventually.TrueAsync(() => ea.Requests.Count == 1, "message ran");
            return 0;
        });

        var request = Assert.Single(ea.Requests);
        Assert.Equal(root.RootTaskId, request.RootTaskId);
        Assert.Equal("user:brendan", request.Principal);
    }

    /// <summary>The body inside an agent-message block, or the prompt itself.</summary>
    private static string BodyOf(string prompt)
    {
        var lines = prompt.Split('\n');
        return lines.Length >= 3 && lines[0].StartsWith("<agent-message", StringComparison.Ordinal) ? lines[1] : prompt;
    }
}
