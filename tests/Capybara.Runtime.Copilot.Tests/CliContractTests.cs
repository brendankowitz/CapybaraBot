using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using Microsoft.Extensions.AI;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 2, Critical items verified against the bundled CLI: session ids use only the characters the CLI accepts,
/// the SDK credit limit never goes below its 30-credit minimum, and host tools declare that they override CLI built-ins.
/// </summary>
public sealed class CliContractTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea-1", a => a with
    {
        Tools = ["list_agents", "ask_agent", "send_message", "delegate_task", "memory_list", "memory_read", "memory_search", "remember", "forget", "propose_change"],
        Budget = new AgentBudget { SessionMaxCredits = 5m },
    });

    [Fact]
    public void Every_session_id_the_host_builds_is_accepted_by_the_cli_alphabet()
    {
        string[] ids =
        [
            SessionIds.Main(Ea.Id),
            SessionIds.Consult(Ea.Id, Guid.NewGuid()),
            SessionIds.Task(Ea.Id, new string('a', 32)),
            SessionIds.Incognito(Ea.Id, "chat_1"),
            TurnTarget.Isolated("routine-run_7").SessionIdFor(Ea.Id),
        ];

        Assert.All(ids, id => Assert.True(SessionIds.IsValid(id), id));
        Assert.False(SessionIds.IsValid("agent:ea:main"));
    }

    [Fact]
    public void Session_ids_round_trip_even_with_hyphenated_agents_and_underscored_keys()
    {
        Assert.True(SessionIds.IsMain(SessionIds.Main(Ea.Id)));
        Assert.True(SessionIds.IsConsult(SessionIds.Consult(Ea.Id, Guid.NewGuid())));
        Assert.False(SessionIds.IsConsult(SessionIds.Main(Ea.Id)));
        Assert.True(SessionIds.TryParseTask(TurnTarget.Isolated("routine-run_7").SessionIdFor(Ea.Id), out var agent, out var key));
        Assert.Equal((Ea.Id, "routine-run_7"), (agent, key));
        Assert.True(SessionIds.TryParseIncognito(SessionIds.Incognito(Ea.Id, "chat_1"), out agent, out key));
        Assert.Equal("chat_1", key);
        Assert.False(SessionIds.TryGetAgent("reviewer_123", out _));
    }

    [Fact]
    public async Task Every_sdk_session_limit_is_at_least_the_cli_minimum()
    {
        using var f = new RuntimeFixture();
        SessionKind[] kinds = [SessionKind.Main, SessionKind.Task, SessionKind.Incognito, SessionKind.Consult];

        foreach (var kind in kinds)
        {
            var config = await f.Configs.CreateConfigAsync(f.Session(Ea, kind), TestContext.Current.CancellationToken);
            Assert.True(config.SessionLimits!.MaxAiCredits >= 30, $"{kind}: {config.SessionLimits.MaxAiCredits}");
        }

        Assert.True(f.Configs.CreateReviewerConfig("reviewer_1", "gpt-5-mini", Ea.Id).SessionLimits!.MaxAiCredits >= 30);
    }

    [Fact]
    public void Every_host_tool_declares_that_it_overrides_a_cli_built_in()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea, SessionKind.Task, taskId: new string('b', 32));

        var tools = f.Tools.GetTools(session, SessionConfigFactory.AvailableToolsFor(session));

        Assert.NotEmpty(tools);
        Assert.All(tools.Cast<AIFunction>(), t => Assert.Equal(true, t.AdditionalProperties.GetValueOrDefault("is_override")));
    }
}
