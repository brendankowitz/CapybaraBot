using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Contracts.Tests;

public sealed class FleetToolsTests
{
    private static readonly string[] Tools = [FleetTools.ListAgents, FleetTools.AskAgent, FleetTools.SendMessage, FleetTools.DelegateTask, FleetTools.Remember, FleetTools.MemoryRead, FleetTools.ProposeChange];

    [Fact]
    public void ForDepth_removes_agent_to_agent_tools_at_max_depth()
    {
        var tools = FleetTools.ForDepth(Tools, Envelope.MaxDepth);

        Assert.DoesNotContain(FleetTools.AskAgent, tools);
        Assert.DoesNotContain(FleetTools.SendMessage, tools);
        Assert.DoesNotContain(FleetTools.DelegateTask, tools);
        Assert.Contains(FleetTools.ListAgents, tools);
    }

    [Fact]
    public void ForDepth_keeps_everything_below_max_depth() =>
        Assert.Equal(Tools, FleetTools.ForDepth(Tools, 0));

    [Fact]
    public void ForConsult_is_read_only()
    {
        var tools = FleetTools.ForConsult(Tools, 1);

        Assert.DoesNotContain(FleetTools.Remember, tools);
        Assert.DoesNotContain(FleetTools.ProposeChange, tools);
        Assert.Contains(FleetTools.MemoryRead, tools);
    }

    [Fact]
    public void Session_ids_are_deterministic()
    {
        var id = AgentId.Parse("ea");
        Assert.Equal("agent_ea_main", SessionIds.Main(id));
        Assert.True(SessionIds.IsMain(SessionIds.Main(id)));
        Assert.False(SessionIds.IsMain(SessionIds.Consult(id, Guid.NewGuid())));
    }
}
