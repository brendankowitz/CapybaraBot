using System.Text.Json;
using Capybara.Connections.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Store;
using Capybara.Policy.Rules;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Broker tools are gated like every other tool (FR-SAF-01, FR-CON-03), dry runs auto-deny (FR-RTN-08), incognito
/// sessions never touch memory (FR-MEM-12) and max-depth sessions get no A2A tools (FR-A2A-06).
/// </summary>
public sealed class SessionToolGatingTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Tools = ["email_read", "email_send", "memory_read", "memory_search", "remember", "ask_agent", "delegate_task"],
        ApprovalRequiredTools = ["email_send"],
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PreToolUseHookInput Input(string tool, string args = "{}") => new()
    {
        ToolName = tool,
        ToolArgs = JsonDocument.Parse(args).RootElement.Clone(),
    };

    private static AIFunction Tool(SessionConfigBase config, string name) =>
        Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!, t => t.Name == name));

    internal static string? Text(object? value) => value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString();

    [Fact]
    public async Task Broker_tools_are_listed_freshly_on_create_and_resume()
    {
        var broker = new FakeBroker("email_read", "email_send");
        using var f = new RuntimeFixture(broker: broker);
        var session = f.Session(Ea);

        var create = await f.Configs.CreateConfigAsync(session, Ct);
        var resume = await f.Configs.ResumeConfigAsync(session, Ct);

        Assert.Equal(2, broker.Listings);
        foreach (SessionConfigBase config in new SessionConfigBase[] { create, resume })
        {
            Assert.Contains(config.Tools!, t => t.Name == "email_send");
            Assert.Null(config.McpServers!.Keys.FirstOrDefault(k => k.Contains("broker", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task A_broker_tool_only_runs_after_the_pre_tool_hook_allowed_that_call()
    {
        var broker = new FakeBroker("email_read");
        using var f = new RuntimeFixture([new PolicyRule { Effect = RuleEffect.Allow, Tool = "email_read" }], broker: broker);
        var session = f.Session(Ea);
        var config = await f.Configs.CreateConfigAsync(session, Ct);
        var tool = Tool(config, "email_read");

        Assert.Equal(GatedFunction.NotAuthorized, Text(await tool.InvokeAsync(new AIFunctionArguments(), Ct)));
        Assert.Empty(broker.Invocations);

        var output = await config.Hooks!.OnPreToolUse!(Input("email_read"), null!);
        Assert.Equal("allow", output!.PermissionDecision);
        Assert.Equal("broker:email_read", Text(await tool.InvokeAsync(new AIFunctionArguments(), Ct)));

        // Single use: a second invocation needs a second gate decision.
        Assert.Equal(GatedFunction.NotAuthorized, Text(await tool.InvokeAsync(new AIFunctionArguments(), Ct)));
        Assert.Single(broker.Invocations);
    }

    [Fact]
    public async Task An_approval_required_broker_tool_waits_for_a_human_and_cannot_run_meanwhile()
    {
        var broker = new FakeBroker("email_send");
        using var f = new RuntimeFixture(broker: broker);
        var session = f.Session(Ea);
        var config = await f.Configs.CreateConfigAsync(session, Ct);

        var output = await config.Hooks!.OnPreToolUse!(Input("email_send", """{"to":"a@example.com"}"""), null!);

        Assert.Equal("deny", output!.PermissionDecision);
        Assert.Single(f.Approvals.Pending());
        Assert.Equal(GatedFunction.NotAuthorized, Text(await Tool(config, "email_send").InvokeAsync(new AIFunctionArguments(), Ct)));
        Assert.Empty(broker.Invocations);
        Assert.Single(session.Turn.Snapshot().PendingApprovals);
    }

    [Fact]
    public async Task An_unreachable_broker_builds_the_session_without_its_tools_and_flags_it()
    {
        using var f = new RuntimeFixture(broker: new FakeBroker { Failure = new HttpRequestException("down") });
        var session = f.Session(Ea);

        var config = await f.Configs.CreateConfigAsync(session, Ct);

        Assert.True(session.BrokerToolsUnavailable);
        Assert.DoesNotContain(config.Tools!, t => t.Name == "email_send");
    }

    [Fact]
    public void Synchronous_builders_refuse_when_broker_tools_exist()
    {
        using var f = new RuntimeFixture(broker: new FakeBroker("email_read"));

        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(f.Session(Ea)));
        Assert.Throws<InvalidOperationException>(() => f.Configs.ResumeConfig(f.Session(Ea)));
    }

    [Fact]
    public async Task A_broker_tool_cannot_shadow_a_fleet_tool()
    {
        using var f = new RuntimeFixture(broker: new FakeBroker("memory_read"));

        var config = await f.Configs.CreateConfigAsync(f.Session(Ea), Ct);

        Assert.IsNotType<GatedFunction>(Assert.Single(config.Tools!, t => t.Name == "memory_read"));
    }

    [Fact]
    public async Task A_dry_run_auto_denies_approval_required_tools_records_them_and_prompts_nobody()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea);
        session.DryRun = true;

        var output = await f.Handlers.OnPreToolUseAsync(session, Input("email_send"));

        Assert.Equal("deny", output.PermissionDecision);
        Assert.Contains("dry run", output.PermissionDecisionReason, StringComparison.Ordinal);
        Assert.Empty(f.Approvals.Pending());
        Assert.Equal(["email_send"], session.Turn.Snapshot().DeniedApprovals);
    }

    [Fact]
    public void Incognito_sessions_get_no_memory_tools()
    {
        using var f = new RuntimeFixture();

        var available = SessionConfigFactory.AvailableToolsFor(f.Session(Ea, SessionKind.Incognito));

        Assert.DoesNotContain(available, t => FleetTools.MemoryReadTools.Contains(t) || FleetTools.MemoryWrite.Contains(t));
        Assert.Contains("email_read", available);
    }

    [Fact]
    public async Task Incognito_memory_tools_never_reach_the_memory_store_even_if_offered()
    {
        var memory = new ForbiddenMemory();
        using var f = new RuntimeFixture(memory: memory);
        var session = f.Session(Ea, SessionKind.Incognito);

        // Offer the memory tools anyway: the provider must still route them to the null store.
        var tools = f.Tools.GetTools(session, [FleetTools.MemoryRead, FleetTools.MemorySearch, FleetTools.Remember]).Cast<AIFunction>().ToList();
        foreach (var tool in tools)
        {
            await tool.InvokeAsync(new AIFunctionArguments { ["path"] = "/user/x.md", ["query"] = "x", ["fact"] = "f", ["description"] = "d" }, Ct);
        }

        Assert.Equal(3, tools.Count);
        Assert.Equal(0, memory.Calls);
        Assert.True(SessionMemory.ContextFor(session, f.Taint).Incognito);
    }

    [Fact]
    public void A_max_depth_task_session_keeps_its_protocol_tools_but_gets_no_agent_to_agent_tools()
    {
        using var f = new RuntimeFixture();

        var available = SessionConfigFactory.AvailableToolsFor(f.Session(Ea, SessionKind.Task, depth: Envelope.MaxDepth, taskId: new string('a', 32)));

        Assert.DoesNotContain(available, FleetTools.AgentToAgent.Contains);
        Assert.DoesNotContain(FleetTools.AnswerTask, available);
        Assert.Contains(FleetTools.CompleteTask, available);
        Assert.Contains(FleetTools.RequestInput, available);
    }

    [Fact]
    public void Protocol_tools_are_only_offered_where_they_act()
    {
        using var f = new RuntimeFixture();

        var main = SessionConfigFactory.AvailableToolsFor(f.Session(Ea));
        var consult = SessionConfigFactory.AvailableToolsFor(f.Session(Ea, SessionKind.Consult));
        var routine = SessionConfigFactory.AvailableToolsFor(f.Session(Ea, SessionKind.Task));

        Assert.Contains(FleetTools.AnswerTask, main);
        Assert.DoesNotContain(FleetTools.CompleteTask, main);
        Assert.DoesNotContain(FleetTools.AnswerTask, consult);
        Assert.DoesNotContain(FleetTools.CompleteTask, routine);
    }

    [Fact]
    public async Task Protocol_tools_bypass_the_gate_only_in_their_own_task_session()
    {
        using var f = new RuntimeFixture();
        var task = f.Session(Ea, SessionKind.Task, taskId: new string('b', 32));

        var inTask = await f.Handlers.OnPreToolUseAsync(task, Input(FleetTools.CompleteTask));
        var inMain = await f.Handlers.OnPreToolUseAsync(f.Session(Ea), Input(FleetTools.CompleteTask));

        Assert.Equal("allow", inTask.PermissionDecision);
        Assert.Equal("deny", inMain.PermissionDecision);
    }

    [Fact]
    public void The_sdk_session_limit_is_never_below_the_cli_minimum()
    {
        using var f = new RuntimeFixture();
        var main = f.Session(Ea);
        main.Envelope = main.Envelope with { Budget = new Budget(0.01m, 1, TimeSpan.FromMinutes(1)) };
        var task = f.Session(Ea, SessionKind.Task);
        task.Envelope = main.Envelope;

        Assert.Null(f.Configs.CreateConfig(main).SessionLimits);
        Assert.Equal(30d, f.Configs.CreateConfig(task).SessionLimits!.MaxAiCredits);
        Assert.Equal(30d, f.Configs.CreateReviewerConfig("reviewer_1", "gpt-5-mini", Ea.Id).SessionLimits!.MaxAiCredits);
        Assert.Equal(45m, SessionConfigFactory.SdkSessionLimit(45m));
    }

    private sealed class FakeBroker(params string[] names) : IBrokerToolSource
    {
        public Exception? Failure { get; init; }

        public int Listings { get; private set; }

        public List<string> Invocations { get; } = [];

        public ValueTask<IReadOnlyList<AIFunction>> GetToolsAsync(string agentId, string sessionId, IReadOnlyCollection<string> availableTools, CancellationToken cancellationToken = default)
        {
            Listings++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.FromResult<IReadOnlyList<AIFunction>>([.. names.Where(availableTools.Contains).Select(n => AIFunctionFactory.Create(
                () =>
                {
                    Invocations.Add(n);
                    return $"broker:{n}";
                },
                n))]);
        }
    }

    /// <summary>Fails the test if anything reaches it.</summary>
    private sealed class ForbiddenMemory : IMemoryStore
    {
        public int Calls { get; private set; }

        public Task<MemoryPage?> ReadAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) => Touch<MemoryPage?>();

        public IReadOnlyList<string> List(MemoryAccessContext context, string? prefix = null)
        {
            Calls++;
            throw new InvalidOperationException("memory touched");
        }

        public Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit = 10, CancellationToken cancellationToken = default) => Touch<IReadOnlyList<IndexEntry>>();

        public Task<string> BuildIndexAsync(MemoryAccessContext context, CancellationToken cancellationToken = default) => Touch<string>();

        public Task<MemoryOperation> RememberAsync(MemoryAccessContext context, string rawPath, string fact, string description, CancellationToken cancellationToken = default) => Touch<MemoryOperation>();

        public Task<MemoryOperation> SupersedeAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) => Touch<MemoryOperation>();

        public Task<MemoryOperation> ForgetAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) => Touch<MemoryOperation>();

        public Task<MemoryOperation> MarkValidatedAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default) => Touch<MemoryOperation>();

        private Task<T> Touch<T>()
        {
            Calls++;
            return Task.FromException<T>(new InvalidOperationException("memory touched"));
        }
    }
}
