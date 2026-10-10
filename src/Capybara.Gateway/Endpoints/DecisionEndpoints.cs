using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Proposals;
using Capybara.Gateway.Security;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Proposals;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// The user's side of agent requests: <c>ask_user</c> questions (FR-A2A-07, the <c>/questions/{id}</c> deep link) and
/// proposed changes to an agent's own config (FR-AGT-06), decided by the human on the authenticated device.
/// </summary>
public static class DecisionEndpoints
{
    public const int MaxAnswerLength = 4_000;

    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder api)
    {
        var questions = api.MapGroup("/questions").WithTags("Questions");

        questions.MapGet("/{id}", (string id, PhoneUserEscalation escalation) =>
            escalation.Find(id) is { } question ? Results.Ok(question.ToDto()) : Results.NotFound());

        questions.MapPost("/{id}", (string id, QuestionAnswerDto request, PhoneUserEscalation escalation) =>
        {
            if (string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Length > MaxAnswerLength)
            {
                return Results.BadRequest($"answer is required and at most {MaxAnswerLength} characters");
            }

            return escalation.SubmitAnswer(id, request.Answer) ? Results.NoContent() : Results.NotFound();
        });

        var proposals = api.MapGroup("/proposals").WithTags("Proposals");

        proposals.MapGet("/", async (string? status, ProposalService service, CancellationToken cancellationToken) =>
        {
            ProposalStatus? filter = null;
            if (status is not null)
            {
                if (!Enum.TryParse<ProposalStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || status.Any(char.IsAsciiDigit))
                {
                    return Results.BadRequest("status must be Pending, Applied, Rejected, Stale or Invalid");
                }

                filter = parsed;
            }

            var list = await service.ListAsync(filter, cancellationToken);
            return Results.Ok(list.OrderByDescending(p => p.CreatedAt).Select(p => p.ToDto()).ToList());
        });

        proposals.MapGet("/{id}", async (string id, ProposalService service, CancellationToken cancellationToken) =>
            await service.GetAsync(id, cancellationToken) is { } proposal ? Results.Ok(proposal.ToDto()) : Results.NotFound());

        // Applying writes the user's config: only ever as the human on this device (FR-AGT-06, FR-SAF-11). The response
        // says when an applied change takes effect, so "Applied" is never mistaken for "already live".
        proposals.MapPost("/{id}", async (string id, ProposalDecisionDto request, HttpContext context, ProposalService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var decided = await service.DecideAsync(id, request.Approve, ConfigPrincipal.Human(context.GetDevice().PrincipalName), cancellationToken);
                return Results.Ok(decided.ToDto() with { Effect = EffectOf(decided) });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        return api;
    }

    /// <summary>When an applied proposal is live (FR-AGT-06, FR-RTN-09); see <c>ConfigurationSync</c>.</summary>
    internal static string? EffectOf(ChangeProposal proposal) => proposal.Status != ProposalStatus.Applied
        ? null
        : proposal.Kind == ProposalKind.Routine
            ? $"Saved as enabled: false. Enable {proposal.Name} from Routines once you have seen its projected monthly cost; if you had already enabled it there, it keeps running unless this change raised that cost."
            : $"Takes effect from {proposal.Agent}'s next turn: its main session is rebuilt from the new configuration (a turn already running finishes on the old one), its CLI restarts when egress, MCP servers or the model provider changed, and its tool self-test runs again.";
}
