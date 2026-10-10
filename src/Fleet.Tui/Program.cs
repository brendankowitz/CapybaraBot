using Fleet.Client;
using Fleet.Tui.Commands;
using Fleet.Tui.Rendering;
using Spectre.Console;

var command = ParsedCommand.Parse(args);
var token = TokenFile.ReadToken();
using var handler = new FleetAuthenticationHandler(new FleetClientOptions { DeviceToken = token }) { InnerHandler = new HttpClientHandler() };
using var http = new HttpClient(handler) { BaseAddress = command.BaseUrl };
var api = new FleetApiClient(http);

try
{
    return await RunAsync(command, api, token);
}
catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

static async Task<int> RunAsync(ParsedCommand command, FleetApiClient api, string token)
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
            await ApprovalsAsync(api, command.Arguments);
            return 0;
        case "thread":
            foreach (var entry in await api.GetThreadAsync())
            {
                AnsiConsole.MarkupLine($"[blue]{Markup.Escape(entry.From)}[/] → [green]{Markup.Escape(entry.To)}[/]: {Markup.Escape(entry.Summary)}");
            }

            return 0;
        case "kill":
            if (command.ReleaseKillSwitch)
            {
                AnsiConsole.MarkupLine(await api.ReleaseKillSwitchAsync() ? "Released." : "Release denied by server.");
            }
            else
            {
                await api.EngageKillSwitchAsync("tui");
                AnsiConsole.MarkupLine("Kill switch engaged.");
            }

            return 0;
        case "budgets":
            AnsiConsole.WriteLine((await api.GetBudgetsAsync())?.ToString() ?? "Budget endpoint unavailable.");
            return 0;
        case "routines":
            foreach (var routine in await api.GetRoutinesAsync())
            {
                AnsiConsole.MarkupLine($"{Markup.Escape(routine.Id)} {routine.Owner} {routine.Kind} projected={routine.ProjectedMonthlyCredits}");
            }

            return 0;
        case "audit":
            AnsiConsole.MarkupLine((await api.VerifyAuditAsync())?.Valid == true ? "[green]Audit valid[/]" : "[red]Audit invalid/unavailable[/]");
            return 0;
        case "dashboard":
            await DashboardAsync(api, command.BaseUrl, token);
            return 0;
        default:
            AnsiConsole.WriteLine("Usage: fleet [--base-url URL] agents|chat <agent>|approvals|thread|kill [--release]|budgets|routines|audit|dashboard");
            return 2;
    }
}

static async Task ChatAsync(FleetApiClient api, string agent)
{
    AnsiConsole.MarkupLine("[grey]Ctrl+C or blank line to exit.[/]");
    while (true)
    {
        var prompt = AnsiConsole.Ask<string>("[blue]>[/]");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var result = await api.SendTurnAsync(agent, prompt);
        AnsiConsole.WriteLine(result?.Content ?? "No response.");
    }
}

static async Task ApprovalsAsync(FleetApiClient api, IReadOnlyList<string> args)
{
    var approvals = await api.GetApprovalsAsync();
    AnsiConsole.Write(Tables.Approvals(approvals));
    if (args.Count >= 3 && args[0] == "decide")
    {
        var approval = approvals.First(a => a.Id == args[1]);
        await api.DecideApprovalAsync(approval.Id, approval.Nonce, args[2]);
    }
}

static async Task DashboardAsync(FleetApiClient api, Uri baseUrl, string token)
{
    await using var realtime = new FleetRealtimeClient(baseUrl, () => Task.FromResult<string?>(token));
    realtime.OnAgentStatusChanged((id, state, unread) => AnsiConsole.MarkupLine($"{Markup.Escape(id)}: {state} unread={unread}"));
    realtime.OnKillSwitchChanged(engaged => AnsiConsole.MarkupLine(engaged ? "[red]Kill switch engaged[/]" : "[green]Kill switch released[/]"));
    await realtime.StartAsync();
    AnsiConsole.Write(Tables.Agents(await api.GetAgentsAsync()));
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
