using System.Globalization;
using Capybara.Client;
using Capybara.Contracts.Routines;
using Capybara.Gateway.Security;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Lifecycle;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// Routine control for the user (FR-RTN-04, FR-RTN-06, FR-RTN-08, FR-RTN-09). Every change is made as
/// <see cref="RoutinePrincipal.Human"/> named from the authenticated device, which only these handlers create.
/// </summary>
/// <remarks>
/// While the kill switch is engaged, re-arming a routine (enable, confirm-resume) is a desktop action, like releasing the
/// switch itself (FR-SAF-06): the kill switch pauses every routine, and only the desktop resumes them.
/// </remarks>
public static class RoutineEndpoints
{
    public const int RunsShown = 20;

    public static IEndpointRouteBuilder MapRoutineEndpoints(this IEndpointRouteBuilder api)
    {
        var routines = api.MapGroup("/routines").WithTags("Routines");

        routines.MapGet("/", (RoutineRegistry registry, RoutineRunHistory history) =>
            TypedResults.Ok(registry.Routines
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => ToDto(r, registry, history))
                .ToList()));

        routines.MapPost("/{id}/enable", async (string id, EnableRoutineDto request, HttpContext context, RoutineRegistry registry, KillSwitch killSwitch, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is not { } routine)
            {
                return Results.NotFound();
            }

            if (ResumeRefused(context, killSwitch) is { } refused)
            {
                return refused;
            }

            var projected = registry.ProjectedMonthlyCredits(routine);
            if (request.AcknowledgedMonthlyCredits != projected)
            {
                return Results.Conflict($"The projected monthly cost is {projected} credits; acknowledge exactly that to enable.");
            }

            try
            {
                registry.Enable(id, request.AcknowledgedMonthlyCredits, Human(context));
            }
            catch (InvalidOperationException ex)
            {
                // The projection moved between the check and the enable (a config reload).
                return Results.Conflict(ex.Message);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }

            await AuditAsync(audit, routine, "enabled", context, $"acknowledged {request.AcknowledgedMonthlyCredits.ToString(CultureInfo.InvariantCulture)} credits/month", cancellationToken);
            return Results.NoContent();
        });

        routines.MapPost("/{id}/disable", async (string id, HttpContext context, RoutineRegistry registry, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is not { } routine)
            {
                return Results.NotFound();
            }

            registry.Disable(id, Human(context));
            await AuditAsync(audit, routine, "disabled", context, null, cancellationToken);
            return Results.NoContent();
        });

        // A billed model loop with every approval-required tool auto-denied (FR-RTN-08); runs even while disabled.
        routines.MapPost("/{id}/dry-run", async (string id, HttpContext context, RoutineRegistry registry, RoutineDispatcher dispatcher, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is null)
            {
                return Results.NotFound();
            }

            var result = await dispatcher.DryRunAsync(id, Human(context), cancellationToken);
            return Results.Ok(new DryRunResultDto(result.Status.ToString(), result.Summary, result.Evidence?.DeniedApprovals ?? [], result.CreditsUsed));
        });

        routines.MapPost("/{id}/confirm-resume", async (string id, HttpContext context, RoutineRegistry registry, RoutineAutoPauser autoPauser, KillSwitch killSwitch, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (registry.Find(id) is not { } routine)
            {
                return Results.NotFound();
            }

            if (ResumeRefused(context, killSwitch) is { } refused)
            {
                return refused;
            }

            autoPauser.ConfirmResume(id, Human(context));
            await AuditAsync(audit, routine, "resumed", context, null, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>Human routine controls are audited like every other control action (FR-SAF-09).</summary>
    private static async Task AuditAsync(IAuditLog audit, RoutineSpec routine, string decision, HttpContext context, string? note, CancellationToken cancellationToken) =>
        await audit.AppendAsync(
            new AuditRecord("routine_control", decision, routine.Owner.Value, Detail: $"routine {routine.Id} by {context.GetDevice().PrincipalName}{(note is null ? string.Empty : "; " + note)}"),
            cancellationToken);

    private static RoutineDto ToDto(RoutineSpec routine, RoutineRegistry registry, RoutineRunHistory history)
    {
        var state = registry.GetState(routine.Id);
        var runs = history.Get(routine.Id)
            .OrderByDescending(r => r.StartedAt)
            .Take(RunsShown)
            .Select(r => new RoutineRunDto(r.StartedAt, r.Outcome.ToString(), r.CreditsSpent))
            .ToList();
        return new RoutineDto(routine.Id, routine.Owner.Value, routine.Kind.ToString(), state.Enabled, state.Paused, state.AutoPaused, registry.ProjectedMonthlyCredits(routine), runs);
    }

    private static RoutinePrincipal Human(HttpContext context) => RoutinePrincipal.Human(context.GetDevice().PrincipalName);

    private static IResult? ResumeRefused(HttpContext context, KillSwitch killSwitch) =>
        killSwitch.IsEngaged && context.GetDevice().Surface != ControlSurface.Desktop
            ? ApiAuthorization.Forbidden("Routines paused by the kill switch can only be resumed from the desktop.")
            : null;
}
