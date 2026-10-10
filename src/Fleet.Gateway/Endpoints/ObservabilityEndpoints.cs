using Fleet.Memory.Store;
using Fleet.Observability.Alerts;
using Fleet.Observability.Cost;
using Fleet.Orchestration.Registry;
using Fleet.Orchestration.Thread;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Fleet.Gateway.Endpoints;

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

        memory.MapGet("/", (WikiStore store) => TypedResults.Ok(store.PendingUserChanges));

        memory.MapPost("/{id}", async Task<Results<NoContent, NotFound>> (string id, PendingChangeDecisionRequest request, WikiStore store, CancellationToken cancellationToken) =>
            await store.ResolveUserChangeAsync(id, request.Apply, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());

        return app;
    }
}
