using Capybara.Client;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;

namespace Capybara.Gateway.Endpoints;

/// <summary>The fleet thread, delegated tasks, alerts and spend (FR-A2A-07, FR-OBS-02, FR-OBS-03, FR-OBS-05).</summary>
public static class ObservabilityEndpoints
{
    public const int MaxTasks = 200;

    public static IEndpointRouteBuilder MapObservabilityEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/thread", (FleetThread thread) => TypedResults.Ok(thread.Entries.Select(e => e.ToDto()).ToList())).WithTags("Observability");

        api.MapGet("/tasks", async (ITaskStore tasks, CancellationToken cancellationToken) =>
        {
            var all = new List<Contracts.Tasks.FleetTask>();
            await foreach (var task in tasks.ListAsync(cancellationToken))
            {
                all.Add(task);
            }

            return TypedResults.Ok(all
                .OrderByDescending(t => t.UpdatedAt)
                .Take(MaxTasks)
                .Select(t => new TaskDto(t.Id, t.Owner.Value, t.Objective, t.State.ToString()))
                .ToList());
        }).WithTags("Observability");

        api.MapGet("/alerts", (FileAlertSink alerts) => TypedResults.Ok(alerts.Recent.Select(a => a.ToDto()).ToList())).WithTags("Observability");

        // Superseded by /budgets for the PWA; kept as raw JSON for older clients.
        api.MapGet("/ledger", (CostLedger ledger, AgentRegistry registry) => TypedResults.Ok(new
        {
            Agents = registry.All.Select(a => ledger.GetStatus(a.Id)).ToList(),
            ByModel = ledger.ByModel(),
            ByRoutine = ledger.ByRoutine(),
        })).WithTags("Observability");

        api.MapGet("/budgets", (CostLedger ledger, AgentRegistry registry, QuotaMonitor quota) =>
        {
            var agents = registry.All
                .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
                .Select(a => ledger.GetStatus(a.Id))
                .ToList();
            var latest = quota.Latest;
            return TypedResults.Ok(new BudgetsDto(
                [.. agents.Select(s => new AgentBudgetDto(s.Agent, s.SpentToday, s.DailyLimit, s.SpentThisMonth, s.MonthlyLimit, s.IsExhausted))],
                new BudgetTotalDto(agents.Sum(s => s.SpentToday), agents.Sum(s => s.DailyLimit)),
                new BudgetTotalDto(agents.Sum(s => s.SpentThisMonth), ledger.FleetMonthlyLimit),
                Lines(ledger.ByModel()),
                Lines(ledger.ByRoutine()),
                latest is null ? null : new QuotaDto(latest.Used, latest.Limit, latest.ResetsAt)));
        }).WithTags("Observability");

        return api;
    }

    private static List<BudgetLineDto> Lines(IReadOnlyDictionary<string, decimal> totals) =>
        [.. totals.OrderByDescending(kv => kv.Value).Select(kv => new BudgetLineDto(kv.Key, kv.Value))];
}
