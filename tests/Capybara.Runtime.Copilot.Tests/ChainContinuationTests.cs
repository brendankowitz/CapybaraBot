using System.Text.Json;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Contracts.Tasks;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 2, N4: a turn resumed after an approval, or after a notice about a chain, continues that chain — depth,
/// budget and root origin — so it can neither reach the user's phone nor regain agent-to-agent reach at depth 0.
/// </summary>
public sealed class ChainContinuationTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with { Tools = ["email_send", "ask_agent"], ApprovalRequiredTools = ["email_send"] }).WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (RuntimeFixture Fixture, CountingEscalation Escalation) Fleet()
    {
        var escalation = new CountingEscalation();
        RuntimeFixture? fixture = null;
        var broker = new EscalatingUserInputBroker(new Lazy<A2ACoordinator>(() => fixture!.Coordinator), new CopilotRuntimeOptions(), NullLogger<EscalatingUserInputBroker>.Instance, escalation);
        fixture = new RuntimeFixture(userInput: broker, fleet: [Ea, Research]);
        return (fixture, escalation);
    }

    [Fact]
    public async Task An_approval_raised_in_an_agent_started_turn_resumes_in_that_chain_and_cannot_reach_the_phone()
    {
        var (f, escalation) = Fleet();
        using var _ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.ApprovalTurns, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);

        // ea is handling research's message: depth 1, a 2-credit carved budget, in a chain the user started.
        var incoming = f.Envelopes.CreateRoot("user", Research.Id, new Budget(2m, 20, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5), rootOrigin: MessageOrigin.User) with { Depth = 1, HopPath = [Research.Id, Ea.Id] };
        var agentTurn = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, f.Envelopes.ContinueFrom(incoming, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
        {
            TurnOrigin = MessageOrigin.Agent,
            TurnFrom = Research.Id,
        };
        var output = await f.Handlers.OnPreToolUseAsync(agentTurn, new PreToolUseHookInput { ToolName = "email_send", ToolArgs = JsonDocument.Parse("{}").RootElement.Clone() });
        Assert.Equal("deny", output.PermissionDecision);
        var approval = Assert.Single(f.Approvals.Pending());

        f.Approvals.Decide(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var notice = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        Assert.Equal(1, notice.Envelope!.Depth);
        Assert.Equal(2m, notice.Envelope.Budget.Credits);
        Assert.Equal(Research.Id, notice.From);

        // The pump continues the notice's chain...
        var runtime = new FakeAgentRuntime(Ea.Id);
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, new InboxPumpOptions(), TimeProvider.System, NullLogger<InboxPump>.Instance);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = pump.RunAsync(stop.Token);
            await Eventually.TrueAsync(() => !runtime.Requests.IsEmpty, "resumed");
            await stop.CancelAsync();
            await run;
        }

        var request = Assert.Single(runtime.Requests);
        Assert.Equal(MessageOrigin.System, request.Origin);
        Assert.Equal((notice.Envelope.RootTaskId, 1, 2m, MessageOrigin.User), (request.Envelope!.RootTaskId, request.Envelope.Depth, request.Envelope.Budget.Credits, request.Envelope.RootOrigin));

        // ...and a turn under it, as the runtime builds it, cannot put a question in front of the user.
        var resumed = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, f.Envelopes.ContinueFrom(request.Envelope!, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
        {
            TurnOrigin = request.Origin,
            TurnFrom = request.From,
        };
        await f.Handlers.OnUserInputRequestAsync(resumed, new UserInputRequest { Question = "Send it to everyone?" });
        Assert.Equal(0, escalation.Count);
    }

    [Fact]
    public async Task Without_a_recorded_context_an_agent_started_resume_fails_closed()
    {
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.ApprovalTurns, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var request = f.Approvals.Request(Ea.Id, SessionIds.Main(Ea.Id), "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.Agent);

        f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var envelope = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct)).Envelope!;
        Assert.Equal(Envelope.MaxDepth, envelope.Depth);
        Assert.Equal(MessageOrigin.Agent, envelope.RootOrigin);
        Assert.Equal(Taint.Tainted, envelope.Taint);
    }

    [Fact]
    public async Task A_user_started_turn_still_resumes_as_a_user_facing_root()
    {
        var (f, escalation) = Fleet();
        using var _ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.ApprovalTurns, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var userTurn = f.Session(Ea);
        userTurn.TurnOrigin = MessageOrigin.User;
        await f.Handlers.OnPreToolUseAsync(userTurn, new PreToolUseHookInput { ToolName = "email_send", ToolArgs = JsonDocument.Parse("{}").RootElement.Clone() });
        var approval = Assert.Single(f.Approvals.Pending());

        f.Approvals.Decide(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var notice = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        var resumed = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, f.Envelopes.ContinueFrom(notice.Envelope!, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
        {
            TurnOrigin = MessageOrigin.System,
        };
        await f.Handlers.OnUserInputRequestAsync(resumed, new UserInputRequest { Question = "Which day?" });
        Assert.Equal(1, escalation.Count);
    }

    [Fact]
    public async Task A_chain_an_event_started_never_reaches_the_phone_even_from_a_root_system_turn()
    {
        var (f, escalation) = Fleet();
        using var _ = f;
        var session = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, f.Envelopes.CreateRoot("routine:inbox", Ea.Id, new Budget(5m, 50, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5), rootOrigin: MessageOrigin.Event))
        {
            TurnOrigin = MessageOrigin.System,
        };

        await f.Handlers.OnUserInputRequestAsync(session, new UserInputRequest { Question = "From the webhook: confirm?" });

        Assert.Equal(0, escalation.Count);
    }

    [Fact]
    public async Task A_failed_sub_task_notice_continues_the_requesters_chain()
    {
        var (f, _) = Fleet();
        using var __ = f;
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(1), rootOrigin: MessageOrigin.User);
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(1)), Ct)).Reference!;
        f.Time.Advance(TimeSpan.FromMinutes(2));
        await f.Coordinator.GetRunnableTaskAsync(Research.Id, taskId, Ct);

        var request = await HandleSingleNoticeAsync(f);

        Assert.Equal(MessageOrigin.System, request.Origin);
        Assert.Equal((root.RootTaskId, 0, MessageOrigin.User), (request.Envelope!.RootTaskId, request.Envelope.Depth, request.Envelope.RootOrigin));
        Assert.Equal(6m, request.Envelope.Budget.Credits); // what the requester kept, not the task's leftover
    }

    [Fact]
    public async Task A_sub_task_that_spent_its_whole_budget_still_reports_its_failure_to_the_requester()
    {
        // Review round 3, I1: the failure notice used to carry the task's spent envelope and was acked with no turn.
        var (f, _) = Fleet();
        using var __ = f;
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10), rootOrigin: MessageOrigin.User);
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(10)), Ct)).Reference!;
        await f.Coordinator.RecordTaskSpendAsync(Research.Id, taskId, 4m, Ct);
        var failed = await f.Coordinator.GetRunnableTaskAsync(Research.Id, taskId, Ct);
        Assert.Equal(FleetTaskState.Failed, failed!.State);

        var request = await HandleSingleNoticeAsync(f);

        Assert.Contains("budget is exhausted", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(6m, request.Envelope!.Budget.Credits);
        Assert.Empty(await f.Inbox.ListAsync(Ea.Id, Ct));
    }

    [Fact]
    public async Task A_task_result_continues_the_requesters_chain_with_the_requesters_budget()
    {
        var (f, _) = Fleet();
        using var __ = f;
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10), rootOrigin: MessageOrigin.User);
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(10)), Ct)).Reference!;
        await f.Coordinator.RecordTaskSpendAsync(Research.Id, taskId, 3.5m, Ct);
        await f.Coordinator.MarkWorkingAsync(Research.Id, taskId, Ct);
        var done = await f.Coordinator.CompleteTaskAsync(Research.Id, taskId, verifiedAgainstObjective: true, [], [], Taint.Clean, Ct);
        Assert.True(done.Success, done.Error);

        var notice = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));

        Assert.Equal((root.RootTaskId, 0, 6m), (notice.Envelope!.RootTaskId, notice.Envelope.Depth, notice.Envelope.Budget.Credits));
    }

    [Fact]
    public async Task A_user_decision_after_the_turns_deadline_resumes_with_a_fresh_one()
    {
        // Review round 3, I2: the resumed turn used to keep the interrupted turn's deadline, so every agent-to-agent call
        // in it failed once the user had taken longer than a turn to decide.
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.ApprovalTurns, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var userTurn = f.Session(Ea); // a user root with a 5-minute deadline
        userTurn.TurnOrigin = MessageOrigin.User;
        await f.Handlers.OnPreToolUseAsync(userTurn, new PreToolUseHookInput { ToolName = "email_send", ToolArgs = JsonDocument.Parse("{}").RootElement.Clone() });
        var approval = Assert.Single(f.Approvals.Pending());

        f.Time.Advance(TimeSpan.FromMinutes(10));
        f.Approvals.Decide(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var notice = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        var resumed = f.Envelopes.ContinueFrom(notice.Envelope!, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20)));
        Assert.Equal(f.Time.GetUtcNow() + TimeSpan.FromMinutes(20), resumed.Deadline);
        Assert.Equal((userTurn.Envelope.RootTaskId, MessageOrigin.User, 0), (resumed.RootTaskId, resumed.RootOrigin, resumed.Depth));

        var delegated = await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), resumed, Research.Id, "x", null, [], new Budget(1m, 10, TimeSpan.FromMinutes(5)), Ct);
        Assert.True(delegated.Success, delegated.Error);
    }

    [Fact]
    public void Only_the_agents_own_person_or_host_started_root_gets_a_fresh_deadline()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var cap = new Budget(10m, 200, TimeSpan.FromMinutes(20));
        var userRoot = f.Envelopes.CreateRoot("user", Ea.Id, cap, TimeSpan.FromMinutes(5), rootOrigin: MessageOrigin.User);
        f.Time.Advance(TimeSpan.FromMinutes(10));
        var renewed = f.Time.GetUtcNow() + cap.Wallclock;

        Assert.Equal(renewed, f.Envelopes.ContinueFrom(userRoot, Ea.Id, cap).Deadline);
        Assert.Equal(userRoot.Deadline, f.Envelopes.ContinueFrom(userRoot with { Depth = 1 }, Ea.Id, cap).Deadline);
        Assert.Equal(userRoot.Deadline, f.Envelopes.ContinueFrom(userRoot with { RootOrigin = MessageOrigin.Agent }, Ea.Id, cap).Deadline);
        Assert.Equal(userRoot.Deadline, f.Envelopes.ContinueFrom(userRoot with { RootOrigin = MessageOrigin.Event }, Ea.Id, cap).Deadline);
        Assert.Equal(userRoot.Deadline, f.Envelopes.ContinueFrom(userRoot, Research.Id, cap).Deadline); // someone else's root
    }

    [Fact]
    public async Task A_host_started_turn_whose_context_was_not_stored_resumes_restricted()
    {
        // Review round 3 (minor): a host-started turn may have been a notice deep in an agent's chain.
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.ApprovalTurns, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var request = f.Approvals.Request(Ea.Id, SessionIds.Main(Ea.Id), "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.System);

        f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        var envelope = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct)).Envelope!;
        Assert.Equal((Envelope.MaxDepth, Taint.Tainted), (envelope.Depth, envelope.Taint));
    }

    private static async Task<AgentTurnRequest> HandleSingleNoticeAsync(RuntimeFixture f)
    {
        var runtime = new FakeAgentRuntime(Ea.Id);
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, new InboxPumpOptions(), TimeProvider.System, NullLogger<InboxPump>.Instance);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = pump.RunAsync(stop.Token);
            await Eventually.TrueAsync(async () => !runtime.Requests.IsEmpty && (await f.Inbox.ListAsync(Ea.Id, Ct)).Count == 0, "notice handled");
            await stop.CancelAsync();
            await run;
        }

        return Assert.Single(runtime.Requests);
    }

    private sealed class CountingEscalation : IUserEscalation
    {
        public int Count { get; private set; }

        public ValueTask<string?> AskAsync(string questionId, AgentId agent, string title, string body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult<string?>("ok");
        }

        public bool SubmitAnswer(string questionId, string answer) => false;
    }
}
