using Capybara.Client;
using Capybara.Tui.Commands;
using Capybara.Tui.Rendering;
using Spectre.Console;

var command = ParsedCommand.Parse(args);
var token = TokenFile.ReadToken();
using var handler = new CapybaraAuthenticationHandler(new CapybaraClientOptions { DeviceToken = token }) { InnerHandler = new HttpClientHandler() };
using var http = new HttpClient(handler) { BaseAddress = command.BaseUrl };
var api = new CapybaraApiClient(http);

try
{
    return await RunAsync(command, api, token);
}
catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or ArgumentException or IOException)
{
    AnsiConsole.MarkupLine($"[red]{Tables.Safe(ex.Message)}[/]");
    return 1;
}

static async Task<int> RunAsync(ParsedCommand command, CapybaraApiClient api, string token)
{
    switch (command.Name)
    {
        case "agents":
            AnsiConsole.Write(Tables.Agents(await api.GetAgentsAsync()));
            return 0;
        case "chat":
            await ChatAsync(api, command.Arguments.Count > 0 ? command.Arguments[0] : throw new ArgumentException("chat requires an agent id."));
            return 0;
        case "approvals":
            await ApprovalsAsync(api, command.Arguments, command.Yes);
            return 0;
        case "proposals":
            await ProposalsAsync(api, SubCommand.Parse("proposals", command.Arguments));
            return 0;
        case "memory":
            await MemoryAsync(api, SubCommand.Parse("memory", command.Arguments), command.OutputPath);
            return 0;
        case "thread":
            foreach (var entry in await api.GetThreadAsync())
            {
                AnsiConsole.MarkupLine($"[blue]{Tables.Safe(entry.From)}[/] → [green]{Tables.Safe(entry.To)}[/]: {Tables.Safe(entry.Summary)}");
            }

            return 0;
        case "kill":
            if (command.ReleaseKillSwitch)
            {
                // Release is desktop-only; a phone-class device token gets 403, which surfaces as an error line.
                await api.ReleaseKillSwitchAsync();
                AnsiConsole.MarkupLine("Released.");
            }
            else
            {
                await api.EngageKillSwitchAsync("tui");
                AnsiConsole.MarkupLine("Kill switch engaged.");
            }

            return 0;
        case "budgets":
            AnsiConsole.Write(Tables.Budgets(await api.GetBudgetsAsync()));
            return 0;
        case "routines":
            await RoutinesAsync(api, SubCommand.Parse("routines", command.Arguments), command.AcknowledgedCredits);
            return 0;
        case "audit":
            AnsiConsole.MarkupLine((await api.VerifyAuditAsync()).Valid ? "[green]Audit valid[/]" : "[red]Audit invalid[/]");
            return 0;
        case "dashboard":
            await DashboardAsync(api, command.BaseUrl, token);
            return 0;
        default:
            AnsiConsole.WriteLine("Usage: capybara [--base-url URL] agents|chat <agent>|approvals [decide <id> allow-once|always|deny [--yes]]|proposals [show|approve|reject <id>]|memory [list [prefix]|show <path>|pending|apply <id>|discard <id>|export [--out FILE]]|routines [enable <id> [--ack CREDITS]|disable <id>|dry-run <id>]|thread|kill [--release]|budgets|audit|dashboard");
            return 2;
    }
}

static async Task ChatAsync(CapybaraApiClient api, string agent)
{
    AnsiConsole.MarkupLine("[grey]Ctrl+C or blank line to exit.[/]");
    while (true)
    {
        var prompt = AnsiConsole.Ask<string>("[blue]>[/]");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        // The last entry before sending anchors the reply lookup if the server gives the turn no message id.
        var recent = await api.GetChatHistoryAsync(agent);
        var before = recent.Count > 0 ? recent[^1] : null;
        var result = await api.SendTurnAsync(agent, prompt);
        if (!result.IsAccepted)
        {
            AnsiConsole.Write(Tables.Plain(result.Summary));
            AnsiConsole.WriteLine();
            continue;
        }

        // Queued through the inbox: the reply lands in the chat history, correlated by message id.
        AnsiConsole.MarkupLine("[grey]Queued, waiting for the reply…[/]");
        var replies = await api.AwaitReplyAsync(agent, result.MessageId, before, TimeSpan.FromSeconds(2), 90);
        if (replies.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No reply yet. It will appear in the chat history when the agent finishes.[/]");
        }

        foreach (var reply in replies)
        {
            AnsiConsole.Write(Tables.Plain(reply.Content));
            AnsiConsole.WriteLine();
        }
    }
}

static async Task ApprovalsAsync(CapybaraApiClient api, IReadOnlyList<string> args, bool assumeYes)
{
    var approvals = await api.GetApprovalsAsync();
    AnsiConsole.Write(Tables.Approvals(approvals));
    if (args.Count >= 3 && args[0] == "decide")
    {
        var approval = approvals.FirstOrDefault(a => a.Id == args[1]) ?? throw new ArgumentException($"No pending approval '{args[1]}'.");

        // Same rules as the PWA card: an unseen, ambiguous or look-alike request can only be denied, and an allow is
        // sent only after the whole request has been shown, revealed.
        var plan = ApprovalReview.Plan(approval, args[2], assumeYes);
        if (plan.Refusal is not null)
        {
            throw new InvalidOperationException(plan.Refusal);
        }

        foreach (var line in plan.Review)
        {
            AnsiConsole.Write(new Text(line));
            AnsiConsole.WriteLine();
        }

        if (plan.NeedsConfirmation && !AnsiConsole.Confirm("Send this decision?", defaultValue: false))
        {
            throw new InvalidOperationException("Not decided: not confirmed.");
        }

        await api.DecideApprovalAsync(approval.Id, approval.Nonce, plan.Choice);
        AnsiConsole.MarkupLine("Decision sent.");
    }
}
static async Task ProposalsAsync(CapybaraApiClient api, SubCommand sub)
{
    switch (sub.Verb)
    {
        case "show":
            var proposal = await api.GetProposalAsync(sub.Target!);
            AnsiConsole.MarkupLine($"[bold]{Tables.Safe(proposal.Kind)}[/] by {Tables.Safe(proposal.Agent)} → {Tables.Safe(proposal.TargetPath)} ({Tables.Safe(proposal.Status)})");
            AnsiConsole.Write(Tables.Plain(proposal.Rationale));
            AnsiConsole.WriteLine();
            AnsiConsole.Write(Tables.Plain(proposal.Diff));
            AnsiConsole.WriteLine();
            break;
        case "approve" or "reject":
            var decided = await api.DecideProposalAsync(sub.Target!, approve: sub.Verb == "approve");
            AnsiConsole.MarkupLine($"Proposal {Tables.Safe(decided.Id)}: {Tables.Safe(decided.Status)}{(decided.Error is null ? string.Empty : $" - {Tables.Safe(decided.Error)}")}");
            break;
        default:
            AnsiConsole.Write(Tables.Proposals(await api.GetProposalsAsync()));
            break;
    }
}

static async Task MemoryAsync(CapybaraApiClient api, SubCommand sub, string? outputPath)
{
    switch (sub.Verb)
    {
        case "show":
            AnsiConsole.Write(Tables.Plain((await api.GetMemoryPageAsync(sub.Target!)).Content));
            AnsiConsole.WriteLine();
            break;
        case "pending":
            AnsiConsole.Write(Tables.PendingMemory(await api.GetPendingMemoryAsync()));
            break;
        case "apply" or "discard":
            await api.DecidePendingMemoryAsync(sub.Target!, apply: sub.Verb == "apply");
            AnsiConsole.MarkupLine("Done.");
            break;
        case "export":
            var path = outputPath ?? "capybara-memory.zip";
            await using (var zip = await api.ExportMemoryAsync())
            await using (var file = File.Create(path))
            {
                await zip.CopyToAsync(file);
            }

            AnsiConsole.MarkupLine($"Exported to {Tables.Safe(path)}.");
            break;
        default:
            AnsiConsole.Write(Tables.MemoryPages(await api.GetMemoryPagesAsync(sub.Target)));
            break;
    }
}

static async Task RoutinesAsync(CapybaraApiClient api, SubCommand sub, decimal? acknowledgedCredits)
{
    switch (sub.Verb)
    {
        case "enable":
            await EnableRoutineAsync(api, sub.Target!, acknowledgedCredits);
            break;
        case "disable":
            await api.DisableRoutineAsync(sub.Target!);
            AnsiConsole.MarkupLine("Disabled.");
            break;
        case "dry-run":
            var result = await api.DryRunRoutineAsync(sub.Target!);
            AnsiConsole.MarkupLine($"Dry run: {Tables.Safe(result.Status)} ({result.Credits:0.##} credits). Auto-denied approvals: {Tables.Safe(result.DeniedApprovals.Count == 0 ? "none" : string.Join(", ", result.DeniedApprovals))}");
            if (!string.IsNullOrEmpty(result.Output))
            {
                AnsiConsole.Write(Tables.Plain(result.Output));
                AnsiConsole.WriteLine();
            }

            break;
        default:
            AnsiConsole.Write(Tables.Routines(await api.GetRoutinesAsync()));
            break;
    }
}

// FR-RTN-09: each heartbeat is a billed loop, so the projected monthly cost must be acknowledged before enabling.
static async Task EnableRoutineAsync(CapybaraApiClient api, string id, decimal? acknowledgedCredits)
{
    var routine = (await api.GetRoutinesAsync()).FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException($"No routine '{id}'.");
    AnsiConsole.MarkupLine($"Routine [bold]{Tables.Safe(routine.Id)}[/] projects [yellow]{routine.ProjectedMonthlyCredits:0.##}[/] credits per month.");
    AnsiConsole.Write(Tables.RoutineRuns(routine));
    var acknowledged = acknowledgedCredits
        ?? (AnsiConsole.Confirm($"Acknowledge {routine.ProjectedMonthlyCredits:0.##} credits/month and enable?", defaultValue: false)
            ? routine.ProjectedMonthlyCredits
            : throw new InvalidOperationException("Not enabled: cost not acknowledged."));
    await api.EnableRoutineAsync(id, acknowledged);
    AnsiConsole.MarkupLine("Enabled.");
}

static async Task DashboardAsync(CapybaraApiClient api, Uri baseUrl, string token)
{
    await using var realtime = new CapybaraRealtimeClient(baseUrl, () => Task.FromResult<string?>(token));
    realtime.OnAgentStatusChanged((id, state, unread) => AnsiConsole.MarkupLine($"{Tables.Safe(id)}: {Tables.Safe(state)} unread={unread}"));
    realtime.OnKillSwitchChanged(engaged => AnsiConsole.MarkupLine(engaged ? "[red]Kill switch engaged[/]" : "[green]Kill switch released[/]"));
    realtime.OnApprovalRequested(a => AnsiConsole.MarkupLine($"[yellow]Approval needed[/]: {Tables.Safe(a.Agent)} → {Tables.Safe(a.Tool)}"));
    await realtime.StartAsync();
    AnsiConsole.Write(Tables.Agents(await api.GetAgentsAsync()));
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
