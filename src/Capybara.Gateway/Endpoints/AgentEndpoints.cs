using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Security;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Capybara.Gateway.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agents").WithTags("Agents");

        group.MapGet("/", (AgentRegistry registry, KillSwitch killSwitch, CostLedger ledger) =>
            TypedResults.Ok(registry.All
                .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
                .Select(a => new AgentStatus(a.Id, a.Name, a.Job, a.Model, killSwitch.IsBlocked(a.Id), ledger.GetStatus(a.Id).IsExhausted))
                .ToList()));

        group.MapPost("/{id}/pause", Results<NoContent, NotFound> (string id, AgentRegistry registry, KillSwitch killSwitch) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return TypedResults.NotFound();
            }

            killSwitch.Pause(agent);
            return TypedResults.NoContent();
        });

        group.MapPost("/{id}/resume", Results<NoContent, NotFound> (string id, AgentRegistry registry, KillSwitch killSwitch) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return TypedResults.NotFound();
            }

            killSwitch.Resume(agent);
            return TypedResults.NoContent();
        });

        // A message typed by the user on an authenticated device: the only MessageOrigin.User entry point.
        group.MapPost("/{id}/turn", async Task<Results<Ok<TurnResult>, NotFound, BadRequest<string>, ProblemHttpResult>> (
            string id,
            TurnRequest request,
            HttpContext context,
            AgentRegistry registry,
            AgentRuntimePool runtimes,
            CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return TypedResults.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return TypedResults.BadRequest("prompt is required");
            }

            try
            {
                var device = context.GetDevice();
                var result = await runtimes.Get(agent).RunTurnAsync(request.Prompt, MessageOrigin.User, $"user:{device.Login}", null, cancellationToken);
                return TypedResults.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/api/runtimes", (RuntimeSupervisor supervisor) => TypedResults.Ok(supervisor.Health)).WithTags("Agents");
        app.MapPost("/api/runtimes/{id}/unquarantine", (string id, RuntimeSupervisor supervisor) =>
        {
            supervisor.Unquarantine(id);
            return TypedResults.NoContent();
        }).WithTags("Agents");

        return app;
    }

    private static AgentId? Resolve(AgentRegistry registry, string id) =>
        AgentId.TryParse(id, out var agent) && registry.Find(agent) is not null ? agent : null;
}
