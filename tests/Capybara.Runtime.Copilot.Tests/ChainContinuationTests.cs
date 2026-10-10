using System.Text.Json;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
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
        var agentTurn = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, EnvelopeFactory.ContinueFrom(incoming, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
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
        var resumed = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, EnvelopeFactory.ContinueFrom(request.Envelope!, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
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
        var resumed = new CapybaraSession(Ea, SessionIds.Main(Ea.Id), SessionKind.Main, EnvelopeFactory.ContinueFrom(notice.Envelope!, Ea.Id, new Budget(10m, 200, TimeSpan.FromMinutes(20))))
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
    public async Task A_failed_sub_task_notice_is_handled_in_the_tasks_chain()
    {
        var (f, _) = Fleet();
        using var __ = f;
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(1), rootOrigin: MessageOrigin.User);
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(1)), Ct)).Reference!;
        f.Time.Advance(TimeSpan.FromMinutes(2));
        await f.Coordinator.GetRunnableTaskAsync(Research.Id, taskId, Ct);
        var runtime = new FakeAgentRuntime(Ea.Id);
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, new InboxPumpOptions(), TimeProvider.System, NullLogger<InboxPump>.Instance);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = pump.RunAsync(stop.Token);
            await Eventually.TrueAsync(() => !runtime.Requests.IsEmpty, "notice handled");
            await stop.CancelAsync();
            await run;
        }

        var request = Assert.Single(runtime.Requests);
        Assert.Equal(MessageOrigin.System, request.Origin);
        Assert.Equal(1, request.Envelope!.Depth);
        Assert.Equal(MessageOrigin.User, request.Envelope.RootOrigin);
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
