using System.Security.Cryptography;
using System.Text;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Diagnostics;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Audit;
using Capybara.Policy.Rules;

namespace Capybara.Policy.Approvals;

/// <summary>
/// Issues and settles approvals (FR-SAF-04): 128-bit nonce, canonical args hash, TTL, single use, fail closed on expiry.
/// </summary>
/// <remarks>
/// With an <see cref="IApprovalStore"/> every state change is journaled and pending approvals are reloaded at
/// construction; approvals that outlived their TTL while the process was down expire on first touch, so they fail
/// closed. With an <see cref="IAuditLog"/> every decision and expiry is appended to the audit trail (FR-SAF-09).
/// State transitions are serialized by one lock; events are raised and audit entries written after it is released.
/// The public API is synchronous, so audit appends block the caller; this is acceptable because the host has no
/// synchronization context and the audit log appends a single short line.
/// </remarks>
public sealed class ApprovalService
{
    private readonly RulesEngine _rules;
    private readonly ApprovalOptions _options;
    private readonly TimeProvider _time;
    private readonly IApprovalStore? _store;
    private readonly IAuditLog? _audit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ApprovalRequest> _approvals = new(StringComparer.Ordinal);

    public ApprovalService(
        RulesEngine rules,
        ApprovalOptions options,
        TimeProvider time,
        IApprovalStore? store = null,
        IAuditLog? audit = null)
    {
        _rules = rules;
        _options = options;
        _time = time;
        _store = store;
        _audit = audit;
        if (store is not null)
        {
            foreach (var request in store.Load())
            {
                _approvals[request.Id] = request;
            }
        }
    }

    public event EventHandler<ApprovalRequest>? Requested;

    /// <summary>Raised once when an unanswered approval fails closed on expiry (FR-OBS-05 alert source).</summary>
    public event EventHandler<ApprovalRequest>? Expired;

    /// <summary>Raised after a human settles an approval.</summary>
    public event EventHandler<ApprovalRequest>? Decided;

    /// <summary>Expires overdue approvals so their events fire even if nobody looks at them. Returns how many expired.</summary>
    public int Sweep()
    {
        var expired = new List<ApprovalRequest>();
        lock (_gate)
        {
            foreach (var request in _approvals.Values.ToList())
            {
                ExpireIfDue(request, expired);
            }
        }

        PublishExpired(expired);
        return expired.Count;
    }

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
        using var activity = CapybaraTelemetry.Policy.StartActivity("approval.request");
        activity?.SetTag("capybara.agent", agent.Value);
        activity?.SetTag("capybara.tool", tool);
        var now = _time.GetUtcNow();
        var ttl = origin == MessageOrigin.User ? _options.InteractiveTtl : _options.UnattendedTtl;
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

        lock (_gate)
        {
            Put(request);
        }

        Requested?.Invoke(this, request);
        return request;
    }

    public ApprovalRequest? Get(string id)
    {
        var expired = new List<ApprovalRequest>();
        ApprovalRequest? result;
        lock (_gate)
        {
            result = _approvals.TryGetValue(id, out var request) ? ExpireIfDue(request, expired) : null;
        }

        PublishExpired(expired);
        return result;
    }

    public IReadOnlyList<ApprovalRequest> Pending()
    {
        var expired = new List<ApprovalRequest>();
        List<ApprovalRequest> pending;
        lock (_gate)
        {
            pending = [.. _approvals.Values.ToList().Select(r => ExpireIfDue(r, expired)).Where(r => r.Status == ApprovalStatus.Pending).OrderBy(r => r.CreatedAt)];
        }

        PublishExpired(expired);
        return pending;
    }

    /// <summary>Settle an approval. The nonce must match and the decider must be the human principal.</summary>
    public ApprovalOutcome Decide(string id, string nonce, ApprovalDecision decision, MessageOrigin decidedBy)
    {
        if (decidedBy != MessageOrigin.User)
        {
            return ApprovalOutcome.NotHuman;
        }

        var expired = new List<ApprovalRequest>();
        ApprovalRequest? settled;
        ApprovalOutcome outcome;
        lock (_gate)
        {
            outcome = DecideLocked(id, nonce, decision, expired, out settled);
        }

        PublishExpired(expired);
        if (settled is null)
        {
            return outcome;
        }

        using (var activity = CapybaraTelemetry.Policy.StartActivity("approval.decide"))
        {
            activity?.SetTag("capybara.agent", settled.Agent.Value);
            activity?.SetTag("capybara.approval.decision", decision.ToString());
        }

        Decided?.Invoke(this, settled);
        Audit(settled, decision switch
        {
            ApprovalDecision.AllowOnce => "allow_once",
            ApprovalDecision.AlwaysAllow => "always_allow",
            _ => "deny",
        });

        if (decision == ApprovalDecision.AlwaysAllow)
        {
            _rules.Add(new PolicyRule
            {
                Effect = RuleEffect.Allow,
                Agent = settled.Agent.Value,
                Tool = settled.Tool,
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
        var expired = new List<ApprovalRequest>();
        var consumed = false;
        lock (_gate)
        {
            foreach (var candidate in _approvals.Values.ToList())
            {
                if (candidate.Agent != agent || candidate.SessionId != sessionId || candidate.Tool != tool || candidate.ArgsHash != argsHash)
                {
                    continue;
                }

                var current = ExpireIfDue(candidate, expired);
                if (current.Status == ApprovalStatus.Approved)
                {
                    Put(current with { Status = ApprovalStatus.Consumed });
                    consumed = true;
                    break;
                }
            }
        }

        PublishExpired(expired);
        return consumed;
    }

    private ApprovalOutcome DecideLocked(
        string id,
        string nonce,
        ApprovalDecision decision,
        List<ApprovalRequest> expired,
        out ApprovalRequest? settled)
    {
        settled = null;
        if (!_approvals.TryGetValue(id, out var current))
        {
            return ApprovalOutcome.NotFound;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current.Nonce), Encoding.ASCII.GetBytes(nonce ?? string.Empty)))
        {
            return ApprovalOutcome.InvalidNonce;
        }

        current = ExpireIfDue(current, expired);
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

        settled = current with { Status = decision == ApprovalDecision.Deny ? ApprovalStatus.Denied : ApprovalStatus.Approved };
        Put(settled);
        return ApprovalOutcome.Accepted;
    }

    /// <summary>Must be called under the gate. Persists first so memory never runs ahead of the journal.</summary>
    private void Put(ApprovalRequest request)
    {
        _store?.Save(request);
        _approvals[request.Id] = request;
    }

    /// <summary>Must be called under the gate. Collects each newly expired approval for publishing.</summary>
    private ApprovalRequest ExpireIfDue(ApprovalRequest request, List<ApprovalRequest> expired)
    {
        if (request.Status is not (ApprovalStatus.Pending or ApprovalStatus.Approved) || _time.GetUtcNow() < request.ExpiresAt)
        {
            return request;
        }

        var expiredRequest = request with { Status = ApprovalStatus.Expired };
        Put(expiredRequest);
        expired.Add(expiredRequest);
        return expiredRequest;
    }

    private void PublishExpired(List<ApprovalRequest> expired)
    {
        foreach (var request in expired)
        {
            CapybaraTelemetry.ApprovalsExpired.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
            Expired?.Invoke(this, request);
            Audit(request, "expired");
        }
    }

    private void Audit(ApprovalRequest request, string decision) =>
        _audit?.AppendAsync(
                new AuditRecord(
                    "approval",
                    decision,
                    request.Agent.Value,
                    request.SessionId,
                    request.Tool,
                    request.ArgsHash,
                    $"approval {request.Id}"),
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
}
