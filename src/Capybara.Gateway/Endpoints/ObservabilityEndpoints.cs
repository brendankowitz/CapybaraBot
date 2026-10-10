using Capybara.Memory.Admin;
using Capybara.Memory.Store;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Thread;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Capybara.Gateway.Endpoints;

public static class ObservabilityEndpoints
{
    public static IEndpointRouteBuilder MapObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ledger", (CostLedger ledger, AgentRegistry registry) => TypedResults.Ok(new
        {
            Agents = registry.All.Select(a => ledger.GetStatus(a.Id)).ToList(),
            ByModel = ledger.ByModel(),
            ByRoutine = ledger.ByRoutine(),
        })).WithTags("Observability");

        app.MapGet("/api/alerts", (InMemoryAlertSink alerts) => TypedResults.Ok(alerts.Recent)).WithTags("Observability");

        app.MapGet("/api/thread", (FleetThread thread) => TypedResults.Ok(thread.Entries)).WithTags("Observability");

        var memory = app.MapGroup("/api/memory/pending").WithTags("Memory");

        memory.MapGet("/", async (MemoryAdmin admin) => TypedResults.Ok(await admin.ListPendingAsync(MemoryAdminPrincipal.Human)));

        memory.MapPost("/{id}", async Task<Results<NoContent, NotFound, Conflict>> (string id, PendingChangeDecisionRequest request, MemoryAdmin admin, CancellationToken cancellationToken) =>
            await admin.ResolveChangeAsync(MemoryAdminPrincipal.Human, id, request.Apply, cancellationToken) switch
            {
                PendingChangeResult.NotFound => TypedResults.NotFound(),
                PendingChangeResult.Stale => TypedResults.Conflict(),
                _ => TypedResults.NoContent(),
            });

        return app;
    }
}
