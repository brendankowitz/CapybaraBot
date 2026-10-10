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
            table.AddRow(Markup.Escape(agent.Id), Markup.Escape(agent.Name), Markup.Escape(agent.Job), Markup.Escape(agent.Model), agent.Paused || agent.BudgetExhausted ? "paused" : "ready");
        }

        return table;
    }

    public static Table Approvals(IEnumerable<ApprovalDto> approvals)
    {
        var table = new Table().AddColumn("Id").AddColumn("Agent").AddColumn("Tool").AddColumn("Risk").AddColumn("Preview");
        foreach (var approval in approvals)
        {
            table.AddRow(Markup.Escape(approval.Id), Markup.Escape(approval.Agent), Markup.Escape(approval.Tool), Markup.Escape(approval.Risk), Markup.Escape(Clean(approval.Preview)));
        }

        return table;
    }

    public static Table Proposals(IEnumerable<ProposalDto> proposals)
    {
        var table = new Table().AddColumn("Id").AddColumn("Agent").AddColumn("Kind").AddColumn("Target").AddColumn("Status").AddColumn("Rationale");
        foreach (var p in proposals)
        {
            table.AddRow(Markup.Escape(p.Id), Markup.Escape(p.Agent), Markup.Escape(p.Kind), Markup.Escape(p.TargetPath), Markup.Escape(p.Status), Markup.Escape(p.Rationale));
        }

        return table;
    }

    public static Table MemoryPages(IEnumerable<MemoryPageInfoDto> pages)
    {
        var table = new Table().AddColumn("Path").AddColumn(new TableColumn("Bytes").RightAligned()).AddColumn("Updated");
        foreach (var page in pages)
        {
            table.AddRow(Markup.Escape(page.Path), page.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), page.UpdatedAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        }

        return table;
    }

    public static Table PendingMemory(IEnumerable<PendingMemoryChangeDto> changes)
    {
        var table = new Table().AddColumn("Id").AddColumn("Kind").AddColumn("Path").AddColumn("Proposed by").AddColumn("Reason");
        foreach (var change in changes)
        {
            table.AddRow(Markup.Escape(change.Id), Markup.Escape(change.Kind), Markup.Escape(change.Path), Markup.Escape(change.ProposedBy), Markup.Escape(change.Reason ?? "-"));
        }

        return table;
    }

    public static Table Routines(IEnumerable<RoutineDto> routines)
    {
        var table = new Table().AddColumn("Id").AddColumn("Owner").AddColumn("Kind").AddColumn("State").AddColumn(new TableColumn("Projected credits/month").RightAligned());
        foreach (var r in routines)
        {
            var state = r.AutoPaused ? "auto-paused" : r.Paused ? "paused" : r.Enabled ? "enabled" : "disabled";
            table.AddRow(Markup.Escape(r.Id), Markup.Escape(r.Owner), Markup.Escape(r.Kind), state, r.ProjectedMonthlyCredits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }

    public static Table RoutineRuns(RoutineDto routine)
    {
        var table = new Table().AddColumn("At").AddColumn("Status").AddColumn("Model").AddColumn(new TableColumn("Credits").RightAligned());
        foreach (var run in routine.LastRuns.Take(20))
        {
            table.AddRow(run.At.ToString("u", System.Globalization.CultureInfo.InvariantCulture), Markup.Escape(run.Status), Markup.Escape(run.Model ?? "-"), run.Credits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }

    public static Table Budgets(BudgetsDto budgets)
    {
        var table = new Table().AddColumn("Scope").AddColumn("Name").AddColumn(new TableColumn("Spent").RightAligned()).AddColumn(new TableColumn("Limit").RightAligned());
        table.AddRow("fleet", "today", Credits(budgets.Daily.Spent), Credits(budgets.Daily.Limit));
        table.AddRow("fleet", "month", Credits(budgets.Monthly.Spent), Credits(budgets.Monthly.Limit));
        foreach (var agent in budgets.Agents)
        {
            table.AddRow("agent", Markup.Escape(agent.Agent) + (agent.Exhausted ? " [red](exhausted)[/]" : string.Empty), $"{Credits(agent.SpentToday)} / {Credits(agent.SpentThisMonth)}", $"{Credits(agent.DailyLimit)} / {Credits(agent.MonthlyLimit)}");
        }

        foreach (var model in budgets.ByModel)
        {
            table.AddRow("model", Markup.Escape(model.Key), Credits(model.Credits), "-");
        }

        foreach (var routine in budgets.ByRoutine)
        {
            table.AddRow("routine", Markup.Escape(routine.Key), Credits(routine.Credits), "-");
        }

        if (budgets.Quota is { } quota)
        {
            table.AddRow("quota", quota.ResetsAt is { } resets ? $"resets {resets:u}" : "account", Credits(quota.Used), Credits(quota.Limit));
        }

        return table;
    }

    private static string Credits(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Agent- and server-supplied text as inert output: control characters (terminal escapes) are dropped.</summary>
    public static string Clean(string text) => string.Concat(text.Where(c => c is '\n' or '\t' || !char.IsControl(c)));

    /// <summary>Untrusted multi-line text (diffs, page bodies) rendered without markup interpretation.</summary>
    public static Text Plain(string text) => new(Clean(text));
}
