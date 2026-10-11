using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Access;
using Capybara.Policy.Rules;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 1: dry runs reach no other agent, memory or proposal (FR-RTN-08); incognito sessions reach no other
/// agent (FR-MEM-12); a finished task's artifacts are never rewritten; gate allows become only the credit the call
/// consumes.
/// </summary>
public sealed class ReviewRound1SessionTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["ask_agent", "send_message", "delegate_task", "remember", "forget", "supersede", "mark_validated", "propose_change", "web_fetch", "memory_read"],
    }).WithPeers("research");

    private static readonly AgentDefinition Research = TestAgents.Create("research").WithPeers("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PreToolUseHookInput Input(string tool) => new() { ToolName = tool, ToolArgs = JsonDocument.Parse("{}").RootElement.Clone() };

    [Theory]
    [InlineData(FleetTools.AskAgent)]
    [InlineData(FleetTools.SendMessage)]
    [InlineData(FleetTools.DelegateTask)]
    [InlineData(FleetTools.AnswerTask)]
    [InlineData(FleetTools.Remember)]
    [InlineData(FleetTools.Forget)]
    [InlineData(FleetTools.Supersede)]
    [InlineData(FleetTools.MarkValidated)]
    [InlineData(FleetTools.ProposeChange)]
    public async Task A_dry_run_denies_tools_that_act_outside_the_test_even_when_rules_allow_them(string tool)
    {
        using var f = new RuntimeFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "*" }]);
        var session = f.Session(Ea);
        session.DryRun = true;

        var output = await f.Handlers.OnPreToolUseAsync(session, Input(tool));

        Assert.Equal("deny", output.PermissionDecision);
        Assert.Equal([tool], session.Turn.Snapshot().DeniedApprovals);
        Assert.Empty(f.Approvals.Pending());
    }

    [Theory]
    [InlineData(FleetTools.CompleteTask)]
    [InlineData(FleetTools.RequestInput)]
    public async Task A_dry_run_denies_task_protocol_tools_before_their_gate_bypass(string tool)
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea, SessionKind.Task, taskId: new string('c', 32));
        session.DryRun = true;

        var output = await f.Handlers.OnPreToolUseAsync(session, Input(tool));

        Assert.Equal("deny", output.PermissionDecision);
        Assert.Contains(tool, session.Turn.Snapshot().DeniedApprovals);
    }

    [Fact]
    public async Task A_dry_run_turn_is_never_offered_to_memory_providers()
    {
        var provider = new RecordingProvider();
        using var f = new RuntimeFixture();
        var memory = new TurnMemory(f.Memory, f.Taint, f.Time, NullLogger<TurnMemory>.Instance, new CompositeMemoryStore(f.Wiki, [provider], f.Time));
        var session = f.Session(Ea);
        session.DryRun = true;

        await memory.IngestAsync(session, "test run", "would send", Ct);

        Assert.Empty(provider.Ingested);
    }

    [Fact]
    public void Incognito_sessions_cannot_reach_other_agents()
    {
        using var f = new RuntimeFixture();

        var available = SessionConfigFactory.AvailableToolsFor(f.Session(Ea, SessionKind.Incognito));

        Assert.DoesNotContain(available, FleetTools.AgentToAgent.Contains);
        Assert.DoesNotContain(FleetTools.AnswerTask, available);
        Assert.Contains("web_fetch", available);
    }

    [Fact]
    public async Task Complete_task_on_a_finished_task_writes_no_artifacts()
    {
        using var f = new RuntimeFixture(fleet: [Ea, Research]);
        var root = f.Envelopes.CreateRoot("user", Ea.Id, new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));
        var taskId = (await f.Coordinator.DelegateTaskAsync(Ea.Id, SessionIds.Main(Ea.Id), root, Research.Id, "x", null, [], new Budget(4m, 10, TimeSpan.FromMinutes(5)), Ct)).Reference!;
        await f.Coordinator.CompleteTaskAsync(Research.Id, taskId, true, [], [], Ct);
        var session = new CapybaraSession(Research, SessionIds.Task(Research.Id, taskId), SessionKind.Task, root, taskId);
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(f.Tools.GetTools(session, [FleetTools.CompleteTask])));

        var reply = SessionToolGatingTests.Text(await tool.InvokeAsync(new AIFunctionArguments { ["result"] = "late", ["verification"] = "v" }, Ct));

        Assert.StartsWith("rejected: task is already Completed", reply, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(f.Options.AgentWorkingDirectory(Ea.Id), "artifacts", "tasks", taskId)));
    }

    [Fact]
    public async Task Allowing_a_host_tool_leaves_no_permission_credit_for_an_unrelated_request()
    {
        using var f = new RuntimeFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "*" }]);
        var session = f.Session(Ea);
        await f.Configs.CreateConfigAsync(session, Ct);

        Assert.Equal("allow", (await f.Handlers.OnPreToolUseAsync(session, Input(FleetTools.MemoryRead))).PermissionDecision);
        Assert.False(f.Allowances.TryConsume(session.SessionId));

        Assert.Equal("allow", (await f.Handlers.OnPreToolUseAsync(session, Input("web_fetch"))).PermissionDecision);
        Assert.True(f.Allowances.TryConsume(session.SessionId));
        Assert.False(f.Allowances.TryConsumeInvocation(session.SessionId, "web_fetch"));
    }

    private sealed class RecordingProvider : IMemoryProvider
    {
        public List<MemoryIngestItem> Ingested { get; } = [];

        public ValueTask<IReadOnlyList<MemoryRecall>> RecallAsync(MemoryAccessContext context, string query, int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<MemoryRecall>>([]);

        public ValueTask IngestAsync(MemoryIngestItem item, CancellationToken cancellationToken = default)
        {
            Ingested.Add(item);
            return ValueTask.CompletedTask;
        }
    }
}
