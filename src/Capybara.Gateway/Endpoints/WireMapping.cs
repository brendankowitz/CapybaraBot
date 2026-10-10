using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Proposals;
using Capybara.Gateway.Realtime;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Thread;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// Domain objects to the client wire shapes (<c>Capybara.Client</c>'s DTOs, the contract's single source of truth),
/// for REST responses and SignalR payloads alike. Enum values travel as their names.
/// </summary>
public static class WireMapping
{
    public static ApprovalDto ToDto(this ApprovalRequest approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new ApprovalDto(
            approval.Id,
            approval.Nonce,
            approval.Agent.Value,
            approval.Tool,
            approval.ArgsHash,
            approval.Preview,
            approval.Risk.ToString(),
            approval.OffersAlwaysAllow,
            approval.CreatedAt,
            approval.ExpiresAt,
            approval.ReviewerVerdict,
            approval.ReviewerRationale,
            approval.Origin.ToString(),
            approval.ArgsJson);
    }

    public static ThreadEntryDto ToDto(this FleetThreadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ThreadEntryDto(entry.At, entry.From.Value, entry.To.Value, entry.Operation.ToString(), entry.Summary, entry.TraceId, entry.Depth, entry.Taint.ToString(), entry.Rejected);
    }

    public static AlertDto ToDto(this Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new AlertDto(alert.Kind, alert.Severity.ToString(), alert.Message, alert.At, alert.Agent);
    }

    public static ProposalDto ToDto(this ChangeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new ProposalDto(
            proposal.Id,
            proposal.Agent.Value,
            proposal.Kind.ToString(),
            proposal.Name,
            proposal.TargetPath,
            proposal.Diff,
            proposal.Rationale,
            proposal.Status.ToString(),
            proposal.CreatedAt,
            proposal.DecidedBy,
            proposal.DecidedAt,
            proposal.Error);
    }

    public static QuestionDto ToDto(this PendingQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new QuestionDto(question.Id, question.Agent.Value, question.Title, question.Body, null, question.ExpiresAt);
    }

    /// <summary>The wire value shared by <c>AgentStatusDto.State</c> and the hub's <c>AgentStatusChanged</c>.</summary>
    public static string ToWire(this AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "working",
        AgentActivityState.NeedsAttention => "needsAttention",
        _ => "idle",
    };
}
