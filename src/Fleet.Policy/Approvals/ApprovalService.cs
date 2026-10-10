using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fleet.Contracts.Agents;
using Fleet.Contracts.Approvals;
using Fleet.Contracts.Diagnostics;
using Fleet.Contracts.Messaging;
using Fleet.Policy.Rules;

namespace Fleet.Policy.Approvals;

/// <summary>
/// Issues and settles approvals (FR-SAF-04): 128-bit nonce, canonical args hash, TTL, single use, fail closed on expiry.
/// </summary>
public sealed class ApprovalService(RulesEngine rules, ApprovalOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _approvals = new(StringComparer.Ordinal);

    public event EventHandler<ApprovalRequest>? Requested;

    /// <summary>Raised once when an unanswered approval fails closed on expiry (FR-OBS-05 alert source).</summary>
    public event EventHandler<ApprovalRequest>? Expired;

    /// <summary>Raised after a human settles an approval.</summary>
    public event EventHandler<ApprovalRequest>? Decided;

    /// <summary>Expires overdue approvals so their events fire even if nobody looks at them.</summary>
    public int Sweep() => _approvals.Values.Count(r => r.Status is ApprovalStatus.Pending && Refresh(r).Status == ApprovalStatus.Expired);

    public ApprovalRequest Request(
        AgentId agent,
        string sessionId,
        string tool,
        string argsHash,
        string preview,
        RiskLevel risk,
        MessageOrigin origin,
        string? reviewerVerdict = null,
        string? reviewerRationale = null)
    {
        using var activity = FleetTelemetry.Policy.StartActivity("approval.request");
        activity?.SetTag("fleet.agent", agent.Value);
        activity?.SetTag("fleet.tool", tool);
        var now = time.GetUtcNow();
        var ttl = origin == MessageOrigin.User ? options.InteractiveTtl : options.UnattendedTtl;
        var request = new ApprovalRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Agent = agent,
            SessionId = sessionId,
            Tool = tool,
            ArgsHash = argsHash,
            Preview = preview,
            Risk = risk,
            Origin = origin,
            ReviewerVerdict = reviewerVerdict,
            ReviewerRationale = reviewerRationale,
            CreatedAt = now,
            ExpiresAt = now + ttl,
        };

        _approvals[request.Id] = request;
        Requested?.Invoke(this, request);
        return request;
    }

    public ApprovalRequest? Get(string id) => _approvals.TryGetValue(id, out var r) ? Refresh(r) : null;

    public IReadOnlyList<ApprovalRequest> Pending() =>
        [.. _approvals.Values.Select(Refresh).Where(r => r.Status == ApprovalStatus.Pending).OrderBy(r => r.CreatedAt)];

    /// <summary>Settle an approval. The nonce must match and the decider must be the human principal.</summary>
    public ApprovalOutcome Decide(string id, string nonce, ApprovalDecision decision, MessageOrigin decidedBy)
    {
        if (decidedBy != MessageOrigin.User)
        {
            return ApprovalOutcome.NotHuman;
        }

        if (!_approvals.TryGetValue(id, out var current))
        {
            return ApprovalOutcome.NotFound;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(current.Nonce),
                System.Text.Encoding.ASCII.GetBytes(nonce ?? string.Empty)))
        {
            return ApprovalOutcome.InvalidNonce;
        }

        current = Refresh(current);
        if (current.Status == ApprovalStatus.Expired)
        {
            return ApprovalOutcome.Expired;
        }

        if (current.Status != ApprovalStatus.Pending)
        {
            return ApprovalOutcome.AlreadyDecided;
        }

        if (decision == ApprovalDecision.AlwaysAllow && !current.OffersAlwaysAllow)
        {
            return ApprovalOutcome.AlwaysNotPermitted;
        }

        var status = decision == ApprovalDecision.Deny ? ApprovalStatus.Denied : ApprovalStatus.Approved;
        if (!_approvals.TryUpdate(id, current with { Status = status }, current))
        {
            return ApprovalOutcome.AlreadyDecided;
        }

        using (var activity = FleetTelemetry.Policy.StartActivity("approval.decide"))
        {
            activity?.SetTag("fleet.agent", current.Agent.Value);
            activity?.SetTag("fleet.approval.decision", decision.ToString());
        }

        Decided?.Invoke(this, current with { Status = status });

        if (decision == ApprovalDecision.AlwaysAllow)
        {
            rules.Add(new PolicyRule
            {
                Effect = RuleEffect.Allow,
                Agent = current.Agent.Value,
                Tool = current.Tool,
                Reason = $"always-allow from approval {id}",
            });
        }

        return ApprovalOutcome.Accepted;
    }

    /// <summary>
    /// Consume a matching approved request for exactly this call. Single use: a second identical call needs a new approval.
    /// </summary>
    public bool TryConsume(AgentId agent, string sessionId, string tool, string argsHash)
    {
        foreach (var candidate in _approvals.Values)
        {
            if (candidate.Agent == agent
                && candidate.SessionId == sessionId
                && candidate.Tool == tool
                && candidate.ArgsHash == argsHash
                && Refresh(candidate).Status == ApprovalStatus.Approved
                && _approvals.TryUpdate(candidate.Id, candidate with { Status = ApprovalStatus.Consumed }, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private ApprovalRequest Refresh(ApprovalRequest request)
    {
        if (request.Status is ApprovalStatus.Pending or ApprovalStatus.Approved && time.GetUtcNow() >= request.ExpiresAt)
        {
            var expired = request with { Status = ApprovalStatus.Expired };
            if (_approvals.TryUpdate(request.Id, expired, request))
            {
                FleetTelemetry.ApprovalsExpired.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
                Expired?.Invoke(this, expired);
            }

            return expired;
        }

        return request;
    }
}
