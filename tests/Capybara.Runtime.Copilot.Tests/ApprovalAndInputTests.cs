using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Tasks;
using Capybara.Orchestration.Coordination;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// FR-A2A-07: only the root agent talks to the human; a sub-agent's question bubbles up as INPUT_REQUIRED.
/// FR-SAF-04: a decided or expired approval queues a notice so the interrupted session resumes.
/// </summary>
public sealed class ApprovalAndInputTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea").WithPeers("research");
    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (RuntimeFixture Fixture, FakeEscalation Escalation) Fleet(string? answer = "Tuesday")
    {
        RuntimeFixture? fixture = null;
        var escalation = new FakeEscalation(answer);
        var broker = new EscalatingUserInputBroker(
            new Lazy<A2ACoordinator>(() => fixture!.Coordinator),
            new CopilotRuntimeOptions { UserEscalationTimeout = TimeSpan.FromMinutes(1) },
            NullLogger<EscalatingUserInputBroker>.Instance,
            escalation);
        fixture = new RuntimeFixture(userInput: broker, fleet: [Ea, Research]);
        return (fixture, escalation);
    }

    private static async Task<(string TaskId, CapybaraSession Session)> DelegateAsync(RuntimeFixture f)
    {
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "book a room", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        var task = (await f.Tasks.GetAsync(taskId, Ct))!;
        return (taskId, new CapybaraSession(Research, SessionIds.Task(Research.Id, taskId), SessionKind.Task, task.Envelope, taskId));
    }

    [Fact]
    public async Task A_sub_agent_question_bubbles_up_as_input_required_and_never_reaches_the_phone()
    {
        var (f, escalation) = Fleet();
        using var _ = f;
        var (taskId, session) = await DelegateAsync(f);

        var response = await f.Handlers.OnUserInputRequestAsync(session, new UserInputRequest { Question = "Which floor?", Choices = ["2", "3"] });

        Assert.Equal(EscalatingUserInputBroker.QuestionPassedUp, response.Answer);
        Assert.Empty(escalation.Questions);
        Assert.Equal(FleetTaskState.InputRequired, (await f.Tasks.GetAsync(taskId, Ct))!.State);
        var question = (await f.Inbox.ListAsync(Ea.Id, Ct)).Single(m => m.TaskId == taskId);
        Assert.Equal(SessionIds.Main(Ea.Id), question.SessionId);
        Assert.Contains("Which floor?\nOptions: 2 | 3", question.Body, StringComparison.Ordinal);
        Assert.Equal(MessageOrigin.Agent, question.Origin);
    }

    [Fact]
    public async Task The_root_agent_escalates_to_the_phone_and_gets_the_human_answer()
    {
        var (f, escalation) = Fleet("Tuesday");
        using var _ = f;

        var response = await f.Handlers.OnUserInputRequestAsync(f.Session(Ea), new UserInputRequest { Question = "Which day?" });

        Assert.Equal("Tuesday", response.Answer);
        var (agent, title, body) = Assert.Single(escalation.Questions);
        Assert.Equal(Ea.Id, agent);
        Assert.Contains(Ea.Name, title, StringComparison.Ordinal);
        Assert.Equal("Which day?", body);
    }

    [Fact]
    public async Task No_answer_from_the_human_tells_the_agent_to_proceed_without_it()
    {
        var (f, _) = Fleet(answer: null);
        using var __ = f;

        var response = await f.Handlers.OnUserInputRequestAsync(f.Session(Ea), new UserInputRequest { Question = "Which day?" });

        Assert.Equal(SessionHandlers.UserUnavailableAnswer, response.Answer);
    }

    [Fact]
    public async Task Without_a_phone_channel_the_root_goes_unanswered_but_sub_agents_still_bubble_up()
    {
        RuntimeFixture? fixture = null;
        var broker = new EscalatingUserInputBroker(new Lazy<A2ACoordinator>(() => fixture!.Coordinator), new CopilotRuntimeOptions(), NullLogger<EscalatingUserInputBroker>.Instance);
        using var f = fixture = new RuntimeFixture(userInput: broker, fleet: [Ea, Research]);
        var (_, session) = await DelegateAsync(f);

        Assert.Null(await broker.AskAsync(f.Session(Ea), "Which day?", [], Ct));
        Assert.Equal(EscalatingUserInputBroker.QuestionPassedUp, await broker.AskAsync(session, "Which floor?", [], Ct));
    }

    [Fact]
    public async Task Dry_runs_and_consults_never_ask_anyone()
    {
        var (f, escalation) = Fleet();
        using var _ = f;
        var dryRun = f.Session(Ea);
        dryRun.DryRun = true;

        await f.Handlers.OnUserInputRequestAsync(dryRun, new UserInputRequest { Question = "?" });
        await f.Handlers.OnUserInputRequestAsync(f.Session(Ea, SessionKind.Consult), new UserInputRequest { Question = "?" });

        Assert.Empty(escalation.Questions);
    }

    [Theory]
    [InlineData(ApprovalDecision.AllowOnce, "approved")]
    [InlineData(ApprovalDecision.Deny, "denied")]
    public async Task A_decided_approval_resumes_the_session_that_asked(ApprovalDecision decision, string word)
    {
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var (taskId, session) = await DelegateAsync(f);
        await DrainAsync(f, Research.Id);
        var request = f.Approvals.Request(Research.Id, session.SessionId, "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.Agent);

        f.Approvals.Decide(request.Id, request.Nonce, decision, MessageOrigin.User);

        var notice = Assert.Single(await f.Inbox.ListAsync(Research.Id, Ct));
        Assert.Equal(MessageOrigin.System, notice.Origin);
        Assert.Equal(session.SessionId, notice.SessionId);
        Assert.Equal(taskId, notice.TaskId);
        Assert.Contains(word, notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_approval_tells_the_agent_not_to_retry()
    {
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        f.Approvals.Request(Ea.Id, SessionIds.Main(Ea.Id), "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.System);

        f.Time.Advance(TimeSpan.FromDays(2));
        f.Approvals.Sweep();

        var notice = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        Assert.Contains("expired", notice.Body, StringComparison.Ordinal);
        Assert.Equal(SessionIds.Main(Ea.Id), notice.SessionId);
    }

    [Fact]
    public async Task An_approval_from_a_finished_consult_resumes_in_the_main_session()
    {
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var request = f.Approvals.Request(Ea.Id, SessionIds.Consult(Ea.Id, Guid.NewGuid()), "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.Agent);

        f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);

        Assert.Null(Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct)).SessionId);
    }

    [Fact]
    public async Task The_pump_resumes_an_isolated_routine_session_for_its_approval_outcome_and_then_ends_it()
    {
        var (f, _) = Fleet();
        using var __ = f;
        using var resumer = new ApprovalResumer(f.Approvals, f.Inbox, f.Alerts, f.Time, NullLogger<ApprovalResumer>.Instance);
        await resumer.StartAsync(Ct);
        var routineSession = SessionIds.Task(Ea.Id, "routine-run-7");
        var request = f.Approvals.Request(Ea.Id, routineSession, "email_send", "hash", "preview", RiskLevel.High, MessageOrigin.System);
        f.Approvals.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);
        var runtime = new FakeAgentRuntime(Ea.Id);
        var pump = new InboxPump(runtime, f.Inbox, f.Coordinator, f.Alerts, new InboxPumpOptions(), TimeProvider.System, NullLogger<InboxPump>.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = pump.RunAsync(stop.Token);
        await Eventually.TrueAsync(() => !runtime.Ended.IsEmpty, "routine session ended after the resume turn");
        await stop.CancelAsync();
        await run;

        var resumed = Assert.Single(runtime.Requests);
        Assert.Equal(routineSession, resumed.Target.SessionIdFor(Ea.Id));
        Assert.Equal(MessageOrigin.System, resumed.Origin);
    }

    private static async Task DrainAsync(RuntimeFixture f, AgentId agent)
    {
        foreach (var message in await f.Inbox.ListAsync(agent, Ct))
        {
            await f.Inbox.AcknowledgeAsync(agent, message.Id, Ct);
        }
    }

    private sealed class FakeEscalation(string? answer) : IUserEscalation
    {
        public List<(AgentId Agent, string Title, string Body)> Questions { get; } = [];

        public ValueTask<string?> AskAsync(string questionId, AgentId agent, string title, string body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Questions.Add((agent, title, body));
            return ValueTask.FromResult(answer);
        }

        public bool SubmitAnswer(string questionId, string answer) => false;
    }
}
