using Capybara.Client;
using Spectre.Console;

namespace Capybara.Tui.Rendering;

public static class Tables
{
    public static Table Agents(IEnumerable<AgentStatusDto> agents)
    {
        var table = new Table().AddColumn("Id").AddColumn("Name").AddColumn("Job").AddColumn("Model").AddColumn("State");
        foreach (var agent in agents)
        {
            table.AddRow(agent.Id, agent.Name, agent.Job, agent.Model, agent.Paused || agent.BudgetExhausted ? "paused" : "ready");
        }

        return table;
    }

    public static Table Approvals(IEnumerable<ApprovalDto> approvals)
    {
        var table = new Table().AddColumn("Id").AddColumn("Agent").AddColumn("Tool").AddColumn("Risk").AddColumn("Preview");
        foreach (var approval in approvals)
        {
            table.AddRow(approval.Id, approval.Agent, approval.Tool, approval.Risk, approval.Preview);
        }

        return table;
    }
}
