using Capybara.Contracts.Messaging;
using Capybara.Gateway.Security;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Capybara.Gateway.Endpoints;

public static class SafetyEndpoints
{
    public static IEndpointRouteBuilder MapSafetyEndpoints(this IEndpointRouteBuilder app)
    {
        var approvals = app.MapGroup("/api/approvals").WithTags("Approvals");

        approvals.MapGet("/", (ApprovalService service) => TypedResults.Ok(service.Pending()));

        // Decisions only come from an authenticated human device, so the origin is always User (FR-A2A-08).
        approvals.MapPost("/{id}", Results<NoContent, NotFound, Conflict<string>, BadRequest<string>> (string id, ApprovalDecisionRequest request, ApprovalService service) =>
            service.Decide(id, request.Nonce ?? string.Empty, request.Decision, MessageOrigin.User) switch
            {
                ApprovalOutcome.Accepted => TypedResults.NoContent(),
                ApprovalOutcome.NotFound => TypedResults.NotFound(),
                ApprovalOutcome.AlreadyDecided or ApprovalOutcome.Expired => TypedResults.Conflict("approval is no longer pending"),
                var other => TypedResults.BadRequest(other.ToString()),
            });

        var kill = app.MapGroup("/api/killswitch").WithTags("Safety");

        kill.MapGet("/", (KillSwitch killSwitch) => TypedResults.Ok(new { killSwitch.IsEngaged, killSwitch.Reason, killSwitch.EngagedAt }));

        // Engaging is allowed from any device; it cancels in-flight work immediately (FR-SAF-06).
        kill.MapPost("/", (KillSwitchRequest request, KillSwitch killSwitch) =>
        {
            killSwitch.Engage(string.IsNullOrWhiteSpace(request.Reason) ? "manual" : request.Reason);
            return TypedResults.NoContent();
        });

        // Releasing requires the desktop.
        kill.MapDelete("/", Results<NoContent, StatusCodeHttpResult> (HttpContext context, KillSwitch killSwitch) =>
        {
            if (context.GetDevice().Surface != ControlSurface.Desktop)
            {
                return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
            }

            killSwitch.Release(ControlSurface.Desktop);
            return TypedResults.NoContent();
        });

        app.MapGet("/api/audit/verify", async (IAuditLog audit, CancellationToken cancellationToken) =>
            TypedResults.Ok(new { Valid = await audit.VerifyAsync(cancellationToken) })).WithTags("Safety");

        return app;
    }
}
