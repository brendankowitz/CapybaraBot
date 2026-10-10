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
            table.AddRow(Safe(agent.Id), Safe(agent.Name), Safe(agent.Job), Safe(agent.Model), agent.Paused || agent.BudgetExhausted ? "paused" : "ready");
        }

        return table;
    }

    public static Table Approvals(IEnumerable<ApprovalDto> approvals)
    {
        // Approval content is shown revealed, never cleaned: invisible and bidi characters appear as ⟦U+XXXX⟧ so the approver sees what would be sent.
        var table = new Table().AddColumn("Id").AddColumn("Agent").AddColumn("Tool").AddColumn("Risk").AddColumn("Allow?").AddColumn("Preview");
        foreach (var approval in approvals)
        {
            var preview = ApprovalPreview.Parse(approval);
            table.AddRow(Revealed(approval.Id), Revealed(approval.Agent), Revealed(approval.Tool), Revealed(approval.Risk), preview.CanApprove ? "yes" : "deny only", Revealed(approval.Preview));
        }

        return table;
    }

    /// <summary>Approval text for markup: <see cref="ApprovalPreview.Reveal"/> then escaped. Unlike <see cref="Safe"/> it drops nothing.</summary>
    public static string Revealed(string? text) => Markup.Escape(ApprovalPreview.Reveal(text));

    public static Table Proposals(IEnumerable<ProposalDto> proposals)
    {
        var table = new Table().AddColumn("Id").AddColumn("Agent").AddColumn("Kind").AddColumn("Target").AddColumn("Status").AddColumn("Rationale");
        foreach (var p in proposals)
        {
            table.AddRow(Safe(p.Id), Safe(p.Agent), Safe(p.Kind), Safe(p.TargetPath), Safe(p.Status), Safe(p.Rationale));
        }

        return table;
    }

    public static Table MemoryPages(IEnumerable<MemoryPageInfoDto> pages)
    {
        var table = new Table().AddColumn("Path").AddColumn(new TableColumn("Bytes").RightAligned()).AddColumn("Updated");
        foreach (var page in pages)
        {
            table.AddRow(Safe(page.Path), page.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), page.UpdatedAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        }

        return table;
    }

    public static Table PendingMemory(IEnumerable<PendingMemoryChangeDto> changes)
    {
        var table = new Table().AddColumn("Id").AddColumn("Kind").AddColumn("Path").AddColumn("Proposed by").AddColumn("Reason");
        foreach (var change in changes)
        {
            table.AddRow(Safe(change.Id), Safe(change.Kind), Safe(change.Path), Safe(change.ProposedBy), Safe(change.Reason ?? "-"));
        }

        return table;
    }

    public static Table Routines(IEnumerable<RoutineDto> routines)
    {
        var table = new Table().AddColumn("Id").AddColumn("Owner").AddColumn("Kind").AddColumn("State").AddColumn(new TableColumn("Projected credits/month").RightAligned());
        foreach (var r in routines)
        {
            var state = r.AutoPaused ? "auto-paused" : r.Paused ? "paused" : r.Enabled ? "enabled" : "disabled";
            table.AddRow(Safe(r.Id), Safe(r.Owner), Safe(r.Kind), state, r.ProjectedMonthlyCredits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        return table;
    }

    public static Table RoutineRuns(RoutineDto routine)
    {
        var table = new Table().AddColumn("At").AddColumn("Status").AddColumn("Model").AddColumn(new TableColumn("Credits").RightAligned());
        foreach (var run in routine.LastRuns.Take(20))
        {
            table.AddRow(run.At.ToString("u", System.Globalization.CultureInfo.InvariantCulture), Safe(run.Status), Safe(run.Model ?? "-"), run.Credits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
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
            table.AddRow("agent", Safe(agent.Agent) + (agent.Exhausted ? " [red](exhausted)[/]" : string.Empty), $"{Credits(agent.SpentToday)} / {Credits(agent.SpentThisMonth)}", $"{Credits(agent.DailyLimit)} / {Credits(agent.MonthlyLimit)}");
        }

        foreach (var model in budgets.ByModel)
        {
            table.AddRow("model", Safe(model.Key), Credits(model.Credits), "-");
        }

        foreach (var routine in budgets.ByRoutine)
        {
            table.AddRow("routine", Safe(routine.Key), Credits(routine.Credits), "-");
        }

        if (budgets.Quota is { } quota)
        {
            table.AddRow("quota", quota.ResetsAt is { } resets ? $"resets {resets:u}" : "account", Credits(quota.Used), Credits(quota.Limit));
        }

        return table;
    }

    private static string Credits(decimal value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Untrusted text as inert output: terminal escapes, other control characters and invisible/bidi format characters
    /// (U+202A–202E, U+2066–2069, zero-width, all Unicode Cf) are dropped. Newlines and tabs survive.
    /// </summary>
    public static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : string.Concat(text.Where(c => c is '\n' or '\t' || !(char.IsControl(c) || IsInvisible(c))));

    /// <summary>For every untrusted table cell or markup argument: cleaned, then escaped so brackets are literal.</summary>
    public static string Safe(string? text) => Markup.Escape(Clean(text));

    /// <summary>Untrusted multi-line text (diffs, page bodies) rendered without markup interpretation.</summary>
    public static Text Plain(string? text) => new(Clean(text));

    private static bool IsInvisible(char c) => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;
}