using System.Diagnostics.Metrics;
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
/// <para>
/// With an <see cref="IApprovalStore"/> every state change is journaled and pending approvals are reloaded at
/// construction; approvals that outlived their TTL while the process was down expire on first touch, so they fail
/// closed.
/// </para>
/// <para>
/// With an <see cref="IAuditLog"/> (FR-SAF-09) a decision is appended to the audit trail <em>before</em> the state change
/// that makes the approval consumable, all under the service lock. If the append fails, <see cref="Decide"/> throws and
/// nothing is committed, so an approval can never be used without its audit record. Expiry only ever makes an approval
/// less usable, so it fails closed instead: the approval expires in memory even when its audit append fails, and is then
/// left unjournaled so it expires, and is audited, again after a restart (an expiry can therefore be audited twice,
/// never zero times). The public API is synchronous, so audit appends block the caller; the host has no
/// synchronization context and the audit log appends one short line.
/// </para>
/// <para>
/// Events and the "Always allow" rule are applied after the commit. A throwing subscriber cannot suppress the rule,
/// the other subscribers or the audit record: the failure is counted (<c>capybara.approvals.handler_failures</c>) and
/// recorded best-effort in the audit log as <c>handler_error</c>.
/// </para>
/// </remarks>
public sealed class ApprovalService
{
    private static readonly Counter<long> HandlerFailures =
        CapybaraTelemetry.Meter.CreateCounter<long>("capybara.approvals.handler_failures", description: "Approval event handlers or learned-rule saves that threw.");

    private static readonly Counter<long> ExpiryAuditFailures =
        CapybaraTelemetry.Meter.CreateCounter<long>("capybara.approvals.expiry_audit_failures", description: "Approval expiries that could not be written to the audit log.");

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
        string? reviewerRationale = null,
        string? argsJson = null)
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
            ArgsJson = argsJson,
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

        Raise(Requested, "Requested", request);
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

    /// <summary>
    /// Settle an approval. The nonce must match and the decider must be the human principal.
    /// </summary>
    /// <exception cref="Exception">The audit append failed. Nothing was committed: the approval is unchanged and not consumable.</exception>
    public ApprovalOutcome Decide(string id, string nonce, ApprovalDecision decision, MessageOrigin decidedBy)
    {
        if (decidedBy != MessageOrigin.User)
        {
            return ApprovalOutcome.NotHuman;
        }

        var expired = new List<ApprovalRequest>();
        ApprovalRequest? settled;
        ApprovalOutcome outcome;
        try
        {
            lock (_gate)
            {
                outcome = DecideLocked(id, nonce, decision, expired, out settled);
            }
        }
        finally
        {
            PublishExpired(expired);
        }

        if (settled is null)
        {
            return outcome;
        }

        using (var activity = CapybaraTelemetry.Policy.StartActivity("approval.decide"))
        {
            activity?.SetTag("capybara.agent", settled.Agent.Value);
            activity?.SetTag("capybara.approval.decision", decision.ToString());
        }

        if (decision == ApprovalDecision.AlwaysAllow)
        {
            Guard("always-allow rule", settled, () => _rules.Add(new PolicyRule
            {
                Effect = RuleEffect.Allow,
                Agent = settled.Agent.Value,
                Tool = settled.Tool,
                Reason = $"always-allow from approval {id}",
            }));
        }

        Raise(Decided, "Decided", settled);
        return ApprovalOutcome.Accepted;
    }

    /// <summary>
    /// Consume a matching approved request for exactly this call. Single use: a second identical call needs a new approval.
    /// </summary>
    public bool TryConsume(AgentId agent, string sessionId, string tool, string argsHash)
    {
        var expired = new List<ApprovalRequest>();
        var consumed = false;
        try
        {
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
        }
        finally
        {
            PublishExpired(expired);
        }

        return consumed;
    }

    private static string AuditDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.AllowOnce => "allow_once",
        ApprovalDecision.AlwaysAllow => "always_allow",
        _ => "deny",
    };

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

        var next = current with { Status = decision == ApprovalDecision.Deny ? ApprovalStatus.Denied : ApprovalStatus.Approved };
        AppendAudit(next, AuditDecision(decision));
        Put(next);
        settled = next;
        return ApprovalOutcome.Accepted;
    }

    /// <summary>Must be called under the gate. Persists first so memory never runs ahead of the journal.</summary>
    private void Put(ApprovalRequest request)
    {
        _store?.Save(request);
        _approvals[request.Id] = request;
    }

    /// <summary>
    /// Must be called under the gate. Expires a due approval, collecting it for publishing. Fails closed: the
    /// in-memory state changes even if the audit append or the journal write fails.
    /// </summary>
    private ApprovalRequest ExpireIfDue(ApprovalRequest request, List<ApprovalRequest> expired)
    {
        if (request.Status is not (ApprovalStatus.Pending or ApprovalStatus.Approved) || _time.GetUtcNow() < request.ExpiresAt)
        {
            return request;
        }

        var expiredRequest = request with { Status = ApprovalStatus.Expired };
        var durable = true;
        try
        {
            AppendAudit(expiredRequest, "expired");
        }
#pragma warning disable CA1031 // Expiry must still take effect; the unjournaled approval is re-expired and re-audited after a restart.
        catch (Exception)
#pragma warning restore CA1031
        {
            ExpiryAuditFailures.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
            durable = false;
        }

        _approvals[expiredRequest.Id] = expiredRequest;
        expired.Add(expiredRequest);
        if (durable)
        {
            try
            {
                _store?.Save(expiredRequest);
            }
#pragma warning disable CA1031 // Already expired in memory; the stale journal entry expires again on reload.
            catch (Exception)
#pragma warning restore CA1031
            {
                ExpiryAuditFailures.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
            }
        }

        return expiredRequest;
    }

    private void PublishExpired(List<ApprovalRequest> expired)
    {
        foreach (var request in expired)
        {
            CapybaraTelemetry.ApprovalsExpired.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
            Raise(Expired, "Expired", request);
        }
    }

    /// <summary>Appends an audit record synchronously; throws when the append fails.</summary>
    private void AppendAudit(ApprovalRequest request, string decision, string? detail = null) =>
        _audit?.AppendAsync(
                new AuditRecord(
                    "approval",
                    decision,
                    request.Agent.Value,
                    request.SessionId,
                    request.Tool,
                    request.ArgsHash,
                    detail ?? $"approval {request.Id}"),
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    /// <summary>Invokes each subscriber separately so one that throws cannot stop the others or the caller.</summary>
    private void Raise(EventHandler<ApprovalRequest>? handler, string name, ApprovalRequest request)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<ApprovalRequest>>())
        {
            Guard($"{name} handler", request, () => subscriber(this, request));
        }
    }

    private void Guard(string what, ApprovalRequest request, Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Notification and rule-persistence failures must not undo or hide a committed decision.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            HandlerFailures.Add(1, new KeyValuePair<string, object?>("agent", request.Agent.Value));
            try
            {
                AppendAudit(request, "handler_error", $"approval {request.Id}: {what} threw {ex.GetType().Name}");
            }
#pragma warning disable CA1031 // Best effort: the audit log is what is failing.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
