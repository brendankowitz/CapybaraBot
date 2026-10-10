using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Proposals;
using Capybara.Contracts.Tasks;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Versioning;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// The fleet tools end to end against the real coordinator: envelope debit (FR-A2A-05), the task protocol
/// (FR-A2A-04/07/09) and change proposals (FR-AGT-06).
/// </summary>
public sealed class FleetToolTests
{
    private const string Schema = """{"type":"object","required":["answer"],"properties":{"answer":{"type":"string"}}}""";

    private static readonly string[] Unverified = ["source freshness"];

    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with { Tools = ["ask_agent", "delegate_task", "propose_change"] }).WithPeers("research", "code");
    private static readonly AgentDefinition Research = TestAgents.Create("research", a => a with { Tools = ["ask_agent"] }).WithPeers("ea");
    private static readonly AgentDefinition Code = TestAgents.Create("code", a => a with { Tools = ["ask_agent"] });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RuntimeFixture Fleet(Orchestration.Proposals.ProposalService? proposals = null) => new(fleet: [Ea, Research, Code], proposals: proposals);

    private static async Task<string?> InvokeAsync(RuntimeFixture f, CapybaraSession session, string tool, AIFunctionArguments args)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(f.Tools.GetTools(session, SessionConfigFactory.AvailableToolsFor(session)), t => t.Name == tool));
        return SessionToolGatingTests.Text(await function.InvokeAsync(args, Ct));
    }

    [Fact]
    public async Task Sequential_consults_through_the_tool_debit_the_session_envelope_until_the_second_fails()
    {
        using var f = Fleet();
        var session = f.Session(Ea);
        session.Envelope = session.Envelope with { Budget = new Budget(3m, 50, TimeSpan.FromMinutes(5)) };

        var first = await InvokeAsync(f, session, FleetTools.AskAgent, new() { ["agent"] = "research", ["question"] = "q1" });
        var second = await InvokeAsync(f, session, FleetTools.AskAgent, new() { ["agent"] = "code", ["question"] = "q2" });

        Assert.Contains("42", first, StringComparison.Ordinal);
        Assert.StartsWith("rejected:", second, StringComparison.Ordinal);
        Assert.Contains("budget", second, StringComparison.Ordinal);
        Assert.Equal(1m, session.Envelope.Budget.Credits);
        Assert.Single(f.Consults.Calls);
    }

    [Fact]
    public async Task Optional_tool_parameters_can_be_omitted_by_the_model()
    {
        using var f = Fleet();
        var session = f.Session(Ea);

        var reply = await InvokeAsync(f, session, FleetTools.DelegateTask, new() { ["agent"] = "research", ["objective"] = "find X" });

        Assert.StartsWith("ok:", reply, StringComparison.Ordinal);
        Assert.Equal(2.5m, session.Envelope.Budget.Credits);
    }

    [Fact]
    public async Task Complete_task_refuses_a_result_that_breaks_the_output_schema()
    {
        using var f = Fleet();
        var (taskId, session) = await DelegateAsync(f, Schema);

        var reply = await InvokeAsync(f, session, FleetTools.CompleteTask, new() { ["result"] = """{"wrong":1}""", ["verification"] = "checked" });

        Assert.StartsWith("rejected: the result does not match the output schema", reply, StringComparison.Ordinal);
        Assert.Equal(FleetTaskState.Submitted, (await f.Tasks.GetAsync(taskId, Ct))!.State);
    }

    [Fact]
    public async Task Complete_task_writes_the_result_into_the_requester_workspace_and_bubbles_it_up_by_reference()
    {
        using var f = Fleet();
        var (taskId, session) = await DelegateAsync(f, Schema);
        f.Taint.MarkTainted(session.SessionId);

        var reply = await InvokeAsync(f, session, FleetTools.CompleteTask, new()
        {
            ["result"] = """{"answer":"blue"}""",
            ["verification"] = "matched the objective",
            ["unverified"] = Unverified,
        });

        Assert.Equal($"ok: {taskId}", reply);
        var task = (await f.Tasks.GetAsync(taskId, Ct))!;
        Assert.Equal(FleetTaskState.Completed, task.State);
        Assert.Equal(["source freshness"], task.Unverified);

        var message = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        Assert.Equal(SessionIds.Main(Ea.Id), message.SessionId);
        Assert.Equal(Taint.Tainted, message.Taint);
        var result = Assert.Single(message.Attachments, a => a.Name == "result.json");
        Assert.Equal($"artifacts/tasks/{taskId}/result.json", result.Uri);
        var onDisk = Path.Combine(f.Options.AgentWorkingDirectory(Ea.Id), "artifacts", "tasks", taskId, "result.json");
        Assert.Equal("""{"answer":"blue"}""", await File.ReadAllTextAsync(onDisk, Ct));
    }

    [Fact]
    public async Task Request_input_moves_the_task_to_input_required_and_asks_the_requester()
    {
        using var f = Fleet();
        var (taskId, session) = await DelegateAsync(f, null);

        var reply = await InvokeAsync(f, session, FleetTools.RequestInput, new() { ["question"] = "Which account?" });

        Assert.StartsWith("ok:", reply, StringComparison.Ordinal);
        Assert.Equal(FleetTaskState.InputRequired, (await f.Tasks.GetAsync(taskId, Ct))!.State);
        Assert.Contains("Which account?", Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Propose_change_records_a_proposal_and_returns_the_rejection_text_when_refused()
    {
        using var config = new ProposalHarness();
        using var f = Fleet(config.Service);
        var session = f.Session(config.Registry.Get(Ea.Id));

        var accepted = await InvokeAsync(f, session, FleetTools.ProposeChange, new() { ["kind"] = "soul", ["content"] = "You are warmer.\n", ["rationale"] = "Tone." });
        var refused = await InvokeAsync(f, session, FleetTools.ProposeChange, new() { ["kind"] = "soul", ["content"] = "x\n", ["rationale"] = new string('r', ProposalService.MaxRationaleLength + 1) });
        var badKind = await InvokeAsync(f, session, FleetTools.ProposeChange, new() { ["kind"] = "grants", ["content"] = "x", ["rationale"] = "r" });

        Assert.StartsWith("proposed:", accepted, StringComparison.Ordinal);
        var proposal = Assert.Single(await config.Service.ListAsync(ProposalStatus.Pending, Ct));
        Assert.Equal(ProposalKind.Soul, proposal.Kind);
        Assert.Equal($"rejected: A rationale of 1 to {ProposalService.MaxRationaleLength} characters is required.", refused);
        Assert.StartsWith("rejected: kind must be", badKind, StringComparison.Ordinal);
    }

    private static async Task<(string TaskId, CapybaraSession Session)> DelegateAsync(RuntimeFixture f, string? schema)
    {
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var delegated = await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "find the colour", schema, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct);
        Assert.True(delegated.Success, delegated.Error);
        var task = (await f.Tasks.GetAsync(delegated.Reference!, Ct))!;
        foreach (var message in await f.Inbox.ListAsync(Research.Id, Ct))
        {
            await f.Inbox.AcknowledgeAsync(Research.Id, message.Id, Ct);
        }

        return (task.Id, new CapybaraSession(Research, SessionIds.Task(Research.Id, task.Id), SessionKind.Task, task.Envelope, task.Id));
    }

    /// <summary>A real proposal service over a one-agent config tree.</summary>
    private sealed class ProposalHarness : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly KillSwitch _killSwitch = new(TimeProvider.System);
        private readonly CapybaraConfigurationHost _host;

        public ProposalHarness()
        {
            var root = Path.Combine(_dir.Path, "config");
            Directory.CreateDirectory(Path.Combine(root, "agents", "ea"));
            File.WriteAllText(Path.Combine(root, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: Assist.\ntools: [propose_change]\n");
            File.WriteAllText(Path.Combine(root, "agents", "ea", "SOUL.md"), "You are EA.\n");
            var initial = CapybaraConfigurationLoader.Load(root);
            Registry = new AgentRegistry(initial.Agents, _killSwitch);
            _host = new CapybaraConfigurationHost(root, initial, Registry, new RulesEngine(initial.Rules), new NoVersioning());
            Service = new ProposalService(new FileProposalStore(Path.Combine(_dir.Path, "proposals")), _host, new InMemoryAuditLog(), new FakeTimeProvider());
        }

        public AgentRegistry Registry { get; }

        public ProposalService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            _host.Dispose();
            _killSwitch.Dispose();
            _dir.Dispose();
        }

        private sealed class NoVersioning : IConfigVersioning
        {
            public ValueTask CommitAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        }
    }
}
