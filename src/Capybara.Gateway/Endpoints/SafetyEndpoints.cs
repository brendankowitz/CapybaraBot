using Capybara.Client;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;

namespace Capybara.Gateway.Endpoints;

/// <summary>Approvals (FR-SAF-04), the kill switch (FR-SAF-06) and the audit log (FR-SAF-09).</summary>
public static class SafetyEndpoints
{
    public const int MaxAudit = 1_000;

    public static IEndpointRouteBuilder MapSafetyEndpoints(this IEndpointRouteBuilder api)
    {
        var approvals = api.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/", (ApprovalService service) => TypedResults.Ok(service.Pending().Select(a => a.ToDto()).ToList()));

        // Decisions only come from an authenticated device, so the deciding origin is always the user (FR-A2A-08); the
        // nonce binds the decision to this one approval and its exact arguments.
        approvals.MapPost("/{id}", (string id, ApprovalDecisionDto request, ApprovalService service) =>
            TryParseDecision(request.Decision, out var decision)
                ? Decide(service, id, request.Nonce ?? string.Empty, decision)
                : Results.BadRequest("decision must be AllowOnce, AlwaysAllow or Deny"));

        // The push notification's action buttons (NotificationFactory.ApprovalActionPath). Same settlement, same nonce
        // check: the query string alone never decides anything, and the device must still be authenticated.
        approvals.MapPost("/{id}/act", (string id, string? nonce, string? decision, ApprovalService service) =>
            string.IsNullOrEmpty(nonce) || !TryParseDecision(decision, out var parsed)
                ? Results.BadRequest("nonce and decision (allow-once, always-allow or deny) are required")
                : Decide(service, id, nonce, parsed));

        var kill = api.MapGroup("/killswitch").WithTags("Safety");

        kill.MapGet("/", (KillSwitch killSwitch) => TypedResults.Ok(new KillSwitchDto(killSwitch.IsEngaged, killSwitch.Reason, killSwitch.EngagedAt)));

        // Engaging is allowed from any device; it cancels in-flight work immediately (FR-SAF-06).
        kill.MapPost("/", (KillSwitchRequestDto request, KillSwitch killSwitch) =>
        {
            killSwitch.Engage(string.IsNullOrWhiteSpace(request.Reason) ? "manual" : request.Reason);
            return TypedResults.NoContent();
        });

        // Releasing requires the desktop.
        kill.MapDelete("/", (KillSwitch killSwitch) =>
        {
            killSwitch.Release(ControlSurface.Desktop);
            return TypedResults.NoContent();
        }).RequireDesktop();

        var audit = api.MapGroup("/audit").WithTags("Safety");

        audit.MapGet("/", async (int? limit, IAuditLog log, CancellationToken cancellationToken) =>
        {
            // Streams the log once, keeping only the newest entries.
            var keep = Math.Clamp(limit ?? 100, 1, MaxAudit);
            var newest = new Queue<AuditEntry>(keep);
            await foreach (var entry in log.ReadAllAsync(cancellationToken))
            {
                if (newest.Count == keep)
                {
                    newest.Dequeue();
                }

                newest.Enqueue(entry);
            }

            return TypedResults.Ok(newest.Reverse().Select(e => new AuditEntryDto(e.Sequence, e.At, e.Kind, e.Decision, e.Agent, e.Tool, e.Detail, e.TraceId)).ToList());
        });

        audit.MapGet("/verify", async (IAuditLog log, CancellationToken cancellationToken) =>
            TypedResults.Ok(new AuditVerifyDto(await log.VerifyAsync(cancellationToken))));

        return api;
    }

    /// <summary>Settles an approval. If its audit record cannot be written nothing is committed and the client gets 500.</summary>
    private static IResult Decide(ApprovalService service, string id, string nonce, ApprovalDecision decision)
    {
        ApprovalOutcome outcome;
        try
        {
            outcome = service.Decide(id, nonce, decision, MessageOrigin.User);
        }
#pragma warning disable CA1031 // Decide throws only when the audit append fails; that decision was not applied (fail closed).
        catch (Exception)
#pragma warning restore CA1031
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The decision could not be recorded in the audit log and was not applied.");
        }

        return outcome switch
        {
            ApprovalOutcome.Accepted => Results.NoContent(),
            ApprovalOutcome.NotFound => Results.NotFound(),
            ApprovalOutcome.AlreadyDecided or ApprovalOutcome.Expired => Results.Conflict("approval is no longer pending"),
            ApprovalOutcome.AlwaysNotPermitted => Results.BadRequest("Always allow is not offered for this approval"),
            _ => Results.BadRequest(outcome.ToString()),
        };
    }

    /// <summary>Enum names (case-insensitive, the REST body) or the push action tokens (<c>allow-once</c>, <c>always-allow</c>, <c>deny</c>).</summary>
    private static bool TryParseDecision(string? value, out ApprovalDecision decision)
    {
        decision = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var name = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return !name.Any(char.IsAsciiDigit) && Enum.TryParse(name, ignoreCase: true, out decision) && Enum.IsDefined(decision);
    }
}
