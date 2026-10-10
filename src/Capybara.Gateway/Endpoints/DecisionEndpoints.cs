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

        // Applying writes the user's config: only ever as the human on this device (FR-AGT-06, FR-SAF-11).
        proposals.MapPost("/{id}", async (string id, ProposalDecisionDto request, HttpContext context, ProposalService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var decided = await service.DecideAsync(id, request.Approve, ConfigPrincipal.Human(context.GetDevice().PrincipalName), cancellationToken);
                return Results.Ok(decided.ToDto());
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
}
