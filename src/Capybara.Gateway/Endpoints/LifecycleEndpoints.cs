using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Gateway.Security;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Lifecycle;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// Creating, duplicating, hiding, archiving and deleting agents (FR-AGT-07, FR-SAF-11): desktop only, and always as
/// the human on the authenticated device.
/// </summary>
public static class LifecycleEndpoints
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/lifecycle/templates", (AgentLifecycleService lifecycle) => TypedResults.Ok(lifecycle.ListTemplates()))
            .RequireDesktop().WithTags("Lifecycle");

        var agents = api.MapGroup("/agents").WithTags("Lifecycle").RequireDesktop();

        agents.MapPost("/", (CreateAgentDto request, HttpContext context, AgentLifecycleService lifecycle, CancellationToken cancellationToken) =>
            !AgentId.TryParse(request.Id, out var id) || string.IsNullOrWhiteSpace(request.Template) || string.IsNullOrWhiteSpace(request.Name)
                ? Task.FromResult(Results.BadRequest("template, a valid id and a name are required"))
                : RunAsync(() => lifecycle.CreateFromTemplateAsync(request.Template, id, request.Name, Human(context), cancellationToken)));

        agents.MapPost("/{id}/duplicate", (string id, DuplicateAgentDto request, HttpContext context, AgentLifecycleService lifecycle, CancellationToken cancellationToken) =>
        {
            if (!AgentId.TryParse(id, out var source))
            {
                return Task.FromResult(Results.NotFound());
            }

            return !AgentId.TryParse(request.Id, out var copy) || string.IsNullOrWhiteSpace(request.Name)
                ? Task.FromResult(Results.BadRequest("a valid id and a name are required"))
                : RunAsync(() => lifecycle.DuplicateAsync(source, copy, request.Name, Human(context), cancellationToken));
        });

        agents.MapPost("/{id}/hide", (string id, SetHiddenDto request, HttpContext context, AgentLifecycleService lifecycle, CancellationToken cancellationToken) =>
            AgentId.TryParse(id, out var agent)
                ? RunAsync(() => lifecycle.SetHiddenAsync(agent, request.Hidden, Human(context), cancellationToken))
                : Task.FromResult(Results.NotFound()));

        agents.MapPost("/{id}/archive", (string id, HttpContext context, AgentLifecycleService lifecycle, CancellationToken cancellationToken) =>
            AgentId.TryParse(id, out var agent)
                ? RunAsync(() => lifecycle.ArchiveAsync(agent, Human(context), cancellationToken))
                : Task.FromResult(Results.NotFound()));

        agents.MapDelete("/{id}", (string id, HttpContext context, AgentLifecycleService lifecycle, CancellationToken cancellationToken) =>
            AgentId.TryParse(id, out var agent)
                ? RunAsync(() => lifecycle.DeleteAsync(agent, Human(context), cancellationToken))
                : Task.FromResult(Results.NotFound()));

        return api;
    }

    private static ConfigPrincipal Human(HttpContext context) => ConfigPrincipal.Human(context.GetDevice().PrincipalName);

    /// <summary>A change that was refused is still 200 with its errors (the contract); only unknown ids and conflicts are not.</summary>
    private static async Task<IResult> RunAsync(Func<ValueTask<ConfigChangeResult>> change)
    {
        try
        {
            var result = await change();
            return Results.Ok(new LifecycleResultDto(result.Applied, result.Errors, result.VersioningError));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (CapybaraConfigurationException ex)
        {
            return Results.Ok(new LifecycleResultDto(false, ex.Errors.Count > 0 ? ex.Errors : [ex.Message]));
        }
    }
}
