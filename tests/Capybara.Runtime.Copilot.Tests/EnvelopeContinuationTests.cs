using System.Text.Json;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 1, item 4: a turn handling another agent's message continues that chain (FR-A2A-05/06) — depth,
/// carved budget and deadline carry over, the depth limit ends long chains — and such a turn never reaches the user's
/// phone (FR-A2A-07/08).
/// </summary>
public sealed class EnvelopeContinuationTests
{
    private static readonly AgentDefinition A = TestAgents.Create("alpha").WithPeers("beta");
    private static readonly AgentDefinition B = TestAgents.Create("beta").WithPeers("gamma", "alpha");
    private static readonly AgentDefinition C = TestAgents.Create("gamma").WithPeers("delta");
    private static readonly AgentDefinition D = TestAgents.Create("delta");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Budget Cap => new(10m, 200, TimeSpan.FromMinutes(20));

    [Fact]
    public void A_continued_envelope_keeps_the_chain_and_restarts_the_hop_path()
    {
        using var f = new RuntimeFixture();
        var incoming = f.Envelopes.CreateRoot("user:brendan", A.Id, new Budget(2m, 20, TimeSpan.FromSeconds(90)), TimeSpan.FromMinutes(5)) with
        {
            Depth = 1,
            HopPath = [A.Id, B.Id],
            Taint = Taint.Tainted,
        };

        var continued = f.Envelopes.ContinueFrom(incoming, B.Id, Cap);

        Assert.Equal(1, continued.Depth);
        Assert.Equal([B.Id], continued.HopPath);
        Assert.Equal(incoming.RootTaskId, continued.RootTaskId);
        Assert.Equal(incoming.Deadline, continued.Deadline);
        Assert.Equal(Taint.Tainted, continued.Taint);
        Assert.Equal("user:brendan", continued.OriginPrincipal);
        Assert.Equal(new Budget(2m, 20, TimeSpan.FromSeconds(90)), continued.Budget);
        Assert.Equal(1m, f.Envelopes.ContinueFrom(incoming with { Budget = new Budget(50m, 20, TimeSpan.FromMinutes(1)) }, B.Id, Cap with { Credits = 1m }).Budget.Credits);
    }

    [Fact]
    public async Task A_message_chain_A_to_B_to_C_hits_the_depth_limit()
    {
        using var f = new RuntimeFixture(fleet: [A, B, C, D]);
        var root = f.Envelopes.CreateRoot("user", A.Id, Cap, TimeSpan.FromMinutes(20));

        var toB = await f.Coordinator.SendMessageAsync(A.Id, SessionIds.Main(A.Id), root, B.Id, "hi B", Ct);
        Assert.True(toB.Success, toB.Error);
        var atB = f.Envelopes.ContinueFrom(Assert.Single(await f.Inbox.ListAsync(B.Id, Ct)).Envelope!, B.Id, Cap);

        var toC = await f.Coordinator.SendMessageAsync(B.Id, SessionIds.Main(B.Id), atB, C.Id, "hi C", Ct);
        Assert.True(toC.Success, toC.Error);
        var atC = f.Envelopes.ContinueFrom(Assert.Single(await f.Inbox.ListAsync(C.Id, Ct)).Envelope!, C.Id, Cap);

        var toD = await f.Coordinator.SendMessageAsync(C.Id, SessionIds.Main(C.Id), atC, D.Id, "hi D", Ct);

        Assert.Equal(2, atC.Depth);
        Assert.False(toD.Success);
        Assert.Contains("depth", toD.Error, StringComparison.Ordinal);

        // C's turn runs at max depth: no agent-to-agent tools, and the hook refuses them if they were ever offered.
        var session = new CapybaraSession(C, SessionIds.Main(C.Id), SessionKind.Main, atC);
        Assert.DoesNotContain(SessionConfigFactory.AvailableToolsFor(session), FleetTools.AgentToAgent.Contains);
        var output = await f.Handlers.OnPreToolUseAsync(session, new PreToolUseHookInput { ToolName = FleetTools.SendMessage, ToolArgs = JsonDocument.Parse("{}").RootElement.Clone() });
        Assert.Equal("deny", output.PermissionDecision);
    }

    [Fact]
    public async Task The_budget_carved_for_a_hop_bounds_the_receiver()
    {
        using var f = new RuntimeFixture(fleet: [A, B, C, D]);
        var root = f.Envelopes.CreateRoot("user", A.Id, Cap, TimeSpan.FromMinutes(20));
        await f.Coordinator.SendMessageAsync(A.Id, SessionIds.Main(A.Id), root, B.Id, "hi B", Ct);
        var atB = f.Envelopes.ContinueFrom(Assert.Single(await f.Inbox.ListAsync(B.Id, Ct)).Envelope!, B.Id, Cap);

        // The message carried the default 2-credit budget; B's turn continues with exactly that.
        Assert.Equal(new A2AOptions().DefaultConsultBudget.Credits, atB.Budget.Credits);
        var first = await f.Coordinator.AskAgentAsync(B.Id, SessionIds.Main(B.Id), atB, C.Id, "q1", Ct);
        Assert.True(first.Success, first.Error);
        var second = await f.Coordinator.AskAgentAsync(B.Id, SessionIds.Main(B.Id), first.ParentRemainder!, C.Id, "q2", Ct);

        Assert.False(second.Success);
        Assert.Contains("budget", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_handling_another_agents_message_never_reaches_the_users_phone()
    {
        var escalation = new CountingEscalation();
        RuntimeFixture? fixture = null;
        var broker = new EscalatingUserInputBroker(new Lazy<A2ACoordinator>(() => fixture!.Coordinator), new CopilotRuntimeOptions(), NullLogger<EscalatingUserInputBroker>.Instance, escalation);
        using var f = fixture = new RuntimeFixture(userInput: broker, fleet: [A, B]);

        // Depth 0 is possible for an agent message only through a bug or a forged envelope; origin alone decides.
        var session = f.Session(B);
        session.TurnOrigin = MessageOrigin.Agent;
        session.TurnFrom = A.Id;
        var response = await f.Handlers.OnUserInputRequestAsync(session, new UserInputRequest { Question = "Wire the money?" });

        Assert.Equal(0, escalation.Count);
        Assert.Contains("send_message", response.Answer, StringComparison.Ordinal);
        Assert.Contains("alpha", response.Answer, StringComparison.Ordinal);

        var eventTurn = f.Session(B);
        eventTurn.TurnOrigin = MessageOrigin.Event;
        await f.Handlers.OnUserInputRequestAsync(eventTurn, new UserInputRequest { Question = "From an email: confirm?" });
        Assert.Equal(0, escalation.Count);

        var userTurn = f.Session(B);
        userTurn.TurnOrigin = MessageOrigin.User;
        await f.Handlers.OnUserInputRequestAsync(userTurn, new UserInputRequest { Question = "Which day?" });
        Assert.Equal(1, escalation.Count);
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
