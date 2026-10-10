using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Routines;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Scheduling.Execution;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 1, item 1 (NFR-02, FR-AGT-05): nothing a message or the store does stops an agent's inbox. M2: a DM
/// that joined a routine run never feeds the routine's delivery. M3: a routine run resumed after an approval still runs
/// and delivers as its routine.
/// </summary>
public sealed class InboxResilienceTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea").WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static readonly InboxPumpOptions Fast = new()
    {
        MaxAttempts = 3,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(20),
        BlockedPollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FleetMessage Message(string body, MessageOrigin origin = MessageOrigin.Agent, string? sessionId = null) => new()
    {
        To = Ea.Id,
        From = origin == MessageOrigin.Agent ? Research.Id : null,
        Origin = origin,
        Body = body,
        SessionId = sessionId,
    };

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
    public async Task A_corrupt_message_file_is_quarantined_alerted_and_the_inbox_keeps_going()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var directory = Path.Combine(f.InboxRoot, "ea");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "00000000000000000001-garbage.json"), "{ not json", Ct);
        await f.Inbox.EnqueueAsync(Message("after the bad one"), Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);

        await RunUntilAsync(new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance), async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "drained");

        Assert.Single(runtime.Requests);
        Assert.True(File.Exists(Path.Combine(directory, "dead", "00000000000000000001-garbage.json")));
        var alert = Assert.Single(f.Alerts.Recent, a => a.Kind == InboxPump.QuarantineAlertKind);
        Assert.DoesNotContain("not json", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_steer_that_throws_means_not_joined_and_the_dm_runs_as_its_own_turn()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var runtime = new ThrowingSteerRuntime(Ea.Id);
        await f.Inbox.EnqueueAsync(Message("hello", MessageOrigin.User), Ct);

        await RunUntilAsync(new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance), async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "DM ran");

        Assert.Equal("hello", Assert.Single(runtime.Requests).Prompt);
    }

    [Fact]
    public async Task A_message_that_cannot_be_planned_is_retried_dead_lettered_and_the_inbox_moves_on()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        await f.Inbox.EnqueueAsync(Message("bad target", MessageOrigin.System, sessionId: "agent_ea_task_not a valid key!"), Ct);
        await f.Inbox.EnqueueAsync(Message("fine"), Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);

        await RunUntilAsync(new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance), async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "drained");

        Assert.Single(runtime.Requests);
        Assert.Contains(f.Alerts.Recent, a => a.Kind == InboxPump.DeadLetterAlertKind);
    }

    [Fact]
    public async Task An_acknowledgement_that_fails_is_retried_without_running_the_turn_again()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var inbox = new FlakyAckInbox(f.Inbox, failures: 2);
        await inbox.EnqueueAsync(Message("one"), Ct);
        await inbox.EnqueueAsync(Message("two"), Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);

        await RunUntilAsync(new InboxPump(runtime, inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance), async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "both acknowledged");

        Assert.Equal(2, runtime.Requests.Count);
        Assert.True(inbox.Failed >= 2);
    }

    [Fact]
    public async Task A_pump_that_faults_is_alerted_and_restarted_by_the_hosted_service()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var inbox = new FlakyAckInbox(f.Inbox, failures: 0, subscribeFailures: 1);
        await inbox.EnqueueAsync(Message("after restart"), Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);
        await using var pool = new AgentRuntimePool(new AgentRegistry([Ea], f.KillSwitch), _ => runtime);
        using var service = new InboxPumpHostedService(pool, inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLoggerFactory.Instance);

        await service.StartAsync(Ct);
        try
        {
            await Eventually.TrueAsync(async () => await f.Inbox.CountAsync(Ea.Id, Ct) == 0, "processed after the restart");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Contains(f.Alerts.Recent, a => a.Kind == InboxPumpHostedService.RestartAlertKind);
        Assert.Single(runtime.Requests);
    }

    [Fact]
    public async Task A_routine_run_a_user_dm_joined_is_never_delivered_to_the_routine_target()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        var runtime = new FakeAgentRuntime(Ea.Id) { Turn = (_, _) => Task.FromResult(TurnResult.Completed("brief, and an answer for you", TurnEvidence.Empty) with { UserMessagesJoined = 1 }) };
        await using var pool = new AgentRuntimePool(new AgentRegistry([Ea], f.KillSwitch), _ => runtime);
        var executor = new CopilotRoutineExecutor(pool, f.Options, NullLogger<CopilotRoutineExecutor>.Instance);
        var delivered = 0;
        executor.Output += (_, _) => delivered++;
        var routine = new RoutineSpec { Id = "hb", Owner = Ea.Id, Kind = TriggerKind.Heartbeat, Prompt = "check" };

        var result = await executor.ExecuteAsync(new RoutineInvocation(routine, RoutineTriggerKind.Heartbeat, DateTimeOffset.UtcNow, "run", false, null, "auto", SessionTarget.Main), Ct);

        Assert.Equal(0, delivered);
        Assert.Equal(RoutineExecutionStatus.Succeeded, result.Status);
        Assert.StartsWith("[a user message joined this run", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_routine_run_resumed_after_its_approval_runs_and_delivers_as_its_routine()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        var waiting = true;
        var runtime = new FakeAgentRuntime(Ea.Id)
        {
            Turn = (request, _) => Task.FromResult(waiting
                ? TurnResult.Completed("waiting for approval", new TurnEvidence(0.5m, "auto", [], [], [], ["approval-1"]))
                : TurnResult.Completed("sent the digest", new TurnEvidence(0.25m, "auto", [], [], [], []))),
        };
        await using var pool = new AgentRuntimePool(new AgentRegistry([Ea], f.KillSwitch), _ => runtime);
        var executor = new CopilotRoutineExecutor(pool, f.Options, NullLogger<CopilotRoutineExecutor>.Instance);
        var delivered = new List<RoutineOutputEventArgs>();
        executor.Output += (_, e) => delivered.Add(e);
        var routine = new RoutineSpec { Id = "digest", Owner = Ea.Id, Kind = TriggerKind.Cron, Prompt = "Send the digest.", Model = "gpt-5-mini", BudgetCreditsPerRun = 2m };

        await executor.ExecuteAsync(new RoutineInvocation(routine, RoutineTriggerKind.Cron, DateTimeOffset.UtcNow, "run-9", false, null, "gpt-5-mini", SessionTarget.IsolatedTask), Ct);
        Assert.NotNull(executor.FindContinuation(Ea.Id, "routine-run-9"));
        Assert.Empty(runtime.Ended);
        delivered.Clear();

        // The approval decision arrives for the isolated session.
        waiting = false;
        await f.Inbox.EnqueueAsync(Message("[approval approval-1] approved", MessageOrigin.System, sessionId: SessionIds.Task(Ea.Id, "routine-run-9")), Ct);
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, Fast, TimeProvider.System, NullLogger<InboxPump>.Instance, executor);
        await RunUntilAsync(pump, () => Task.FromResult(!runtime.Ended.IsEmpty), "resumed run finished");

        var resumed = runtime.Requests.Last();
        Assert.Equal("digest", resumed.RoutineId);
        Assert.Equal("gpt-5-mini", resumed.Model);
        Assert.Equal(1.5m, resumed.Budget!.Credits);
        Assert.Equal("sent the digest", Assert.Single(delivered).Content);
        Assert.Null(executor.FindContinuation(Ea.Id, "routine-run-9"));
    }

    /// <summary>Steering always throws, as when the CLI died mid-turn.</summary>
    private sealed class ThrowingSteerRuntime(AgentId agent) : IAgentRuntime
    {
        private readonly FakeAgentRuntime _inner = new(agent);

        public AgentId Agent => _inner.Agent;

        public AgentDefinition Definition => _inner.Definition;

        public Task UpdateDefinitionAsync(AgentDefinition definition, CancellationToken cancellationToken) => _inner.UpdateDefinitionAsync(definition, cancellationToken);

        public System.Collections.Concurrent.ConcurrentQueue<AgentTurnRequest> Requests => _inner.Requests;

        public Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken) => _inner.RunTurnAsync(request, cancellationToken);

        public Task<TurnResult?> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("session disposed");

        public Task SetModelAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<IncognitoSessionInfo>> ListIncognitoSessionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IncognitoSessionInfo>>([]);

        public Task<Orchestration.Coordination.ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken) =>
            _inner.ConsultAsync(sessionId, question, envelope, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A real inbox whose acknowledgements (and first subscriptions) fail a set number of times.</summary>
    private sealed class FlakyAckInbox(IInboxStore inner, int failures, int subscribeFailures = 0) : IInboxStore
    {
        private int _failures = failures;
        private int _subscribeFailures = subscribeFailures;

        public int Failed { get; private set; }

        public event EventHandler<AgentId>? MessageAvailable
        {
            add
            {
                if (Interlocked.Decrement(ref _subscribeFailures) >= 0)
                {
                    throw new IOException("inbox unavailable");
                }

                inner.MessageAvailable += value;
            }

            remove => inner.MessageAvailable -= value;
        }

        public event EventHandler<AgentId>? MessageQuarantined
        {
            add => inner.MessageQuarantined += value;
            remove => inner.MessageQuarantined -= value;
        }

        public ValueTask EnqueueAsync(FleetMessage message, CancellationToken cancellationToken = default) => inner.EnqueueAsync(message, cancellationToken);

        public ValueTask<FleetMessage?> PeekAsync(AgentId agent, CancellationToken cancellationToken = default) => inner.PeekAsync(agent, cancellationToken);

        public ValueTask<IReadOnlyList<FleetMessage>> ListAsync(AgentId agent, CancellationToken cancellationToken = default) => inner.ListAsync(agent, cancellationToken);

        public ValueTask AcknowledgeAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                Failed++;
                throw new IOException("disk full");
            }

            return inner.AcknowledgeAsync(agent, messageId, cancellationToken);
        }

        public ValueTask DeadLetterAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default) => inner.DeadLetterAsync(agent, messageId, cancellationToken);

        public ValueTask<int> CountAsync(AgentId agent, CancellationToken cancellationToken = default) => inner.CountAsync(agent, cancellationToken);
    }
}
