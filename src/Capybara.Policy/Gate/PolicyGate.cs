using Capybara.Contracts.Approvals;
using Capybara.Contracts.Diagnostics;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Canonicalization;
using Capybara.Policy.Provenance;
using Capybara.Policy.Review;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;

namespace Capybara.Policy.Gate;

/// <summary>
/// The layered gate every tool call passes (FR-SAF-01):
/// (0) kill switch / pause, (1) static deny (allowlist + deny rules), (2) deterministic rules — ask-first beats allow,
/// (3) taint and provenance, (4) reviewer for whatever is unresolved, (5) human approval. Fails closed.
/// </summary>
public sealed class PolicyGate(
    RulesEngine rules,
    TaintTracker taint,
    ApprovalService approvals,
    KillSwitch killSwitch,
    IAuditLog audit,
    IToolCallReviewer? reviewer = null)
{
    /// <summary><see cref="GateVerdict.Layer"/> of a call auto-denied because it would need approval in a dry run.</summary>
    public const string DryRunLayer = "dry-run";

    public async Task<GateVerdict> EvaluateAsync(ToolCallContext call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var activity = CapybaraTelemetry.Policy.StartActivity("gate.evaluate");
        activity?.SetTag("capybara.agent", call.Agent.Id.Value);
        activity?.SetTag("capybara.tool", call.Tool);
        var argsHash = CanonicalJson.Hash(call.Args);
        GateVerdict verdict;
        try
        {
            verdict = await DecideAsync(call, argsHash, cancellationToken);
        }
#pragma warning disable CA1031 // Fail closed on any unexpected error.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            verdict = GateVerdict.Deny("error", $"gate error: {ex.GetType().Name}");
        }

        activity?.SetTag("capybara.gate.outcome", verdict.Outcome.ToString());
        activity?.SetTag("capybara.gate.layer", verdict.Layer);
        CapybaraTelemetry.GateDecisions.Add(1, new("outcome", verdict.Outcome.ToString()), new("layer", verdict.Layer), new("agent", call.Agent.Id.Value));
        await audit.AppendAsync(
            new AuditRecord(
                Kind: "tool_call",
                Decision: $"{verdict.Outcome}:{verdict.Layer}",
                Agent: call.Agent.Id,
                SessionId: call.SessionId,
                Tool: call.Tool,
                ArgsHash: argsHash,
                Detail: verdict.Approval is null ? verdict.Reason : $"{verdict.Reason} (approval {verdict.Approval.Id})",
                TraceId: call.TraceId),
            CancellationToken.None);

        return verdict;
    }

    private async Task<GateVerdict> DecideAsync(ToolCallContext call, string argsHash, CancellationToken cancellationToken)
    {
        var agent = call.Agent;

        // Layer 0: kill switch and per-agent pause.
        if (killSwitch.IsBlocked(agent.Id))
        {
            return GateVerdict.Deny("kill-switch", "fleet or agent is paused");
        }

        // Layer 1: static deny. Tools outside the allowlist never run, whatever the rules say.
        if (!agent.Tools.Any(pattern => ToolPattern.IsMatch(pattern, call.Tool)))
        {
            return GateVerdict.Deny("static", $"'{call.Tool}' is not in {agent.Id}'s tool allowlist");
        }

        var match = rules.Evaluate(agent.Id, call.Tool, call.Args);
        if (match?.Rule.Effect == RuleEffect.Deny)
        {
            return GateVerdict.Deny("static", match.Rule.Reason ?? "deny rule");
        }

        // A previously granted, single-use approval for these exact args. A dry run never spends a real approval.
        if (!call.DryRun && approvals.TryConsume(agent.Id, call.SessionId, call.Tool, argsHash))
        {
            return GateVerdict.Allow("approval", "consumed matching approval");
        }

        // Layer 2: deterministic rules. Approval-required tools are always ask-first.
        var approvalRequired = agent.ApprovalRequiredTools.Any(pattern => ToolPattern.IsMatch(pattern, call.Tool));
        if (approvalRequired || match?.Rule.Effect == RuleEffect.Ask)
        {
            return Ask(call, argsHash, RiskLevel.High, approvalRequired ? "approval-required tool" : match!.Rule.Reason ?? "ask-first rule");
        }

        // Layer 3: taint and provenance.
        var tainted = taint.IsTainted(call.SessionId);
        if (match?.Rule.Effect == RuleEffect.AllowUnlessTainted)
        {
            if (!tainted || (match.ArgumentValue is { } destination && match.Rule.DestinationAllowlist.Any(d => Glob.IsMatch(d, destination))))
            {
                return GateVerdict.Allow("rules", match.Rule.Reason ?? "allow (clean or allowlisted destination)");
            }

            return Ask(call, argsHash, RiskLevel.Medium, "consequential action in a tainted session");
        }

        if (match?.Rule.Effect == RuleEffect.Allow)
        {
            return GateVerdict.Allow("rules", match.Rule.Reason ?? "allow rule");
        }

        // Layer 4: reviewer for unresolved calls. It can block or escalate, never bypass layers 1–3.
        if (reviewer is not null)
        {
            // Over-limit args are never shown to the model in full, so it cannot vouch for them: escalate deterministically.
            var review = ReviewerRubric.ExceedsLimit(call)
                ? ReviewerRubric.TruncatedResult
                : await reviewer.ReviewAsync(call, cancellationToken);
            return review.Verdict switch
            {
                ReviewVerdict.Allow when !tainted => GateVerdict.Allow("reviewer", review.Rationale),
                ReviewVerdict.Block => GateVerdict.Deny("reviewer", review.Rationale),
                _ => Ask(call, argsHash, RiskLevel.Medium, review.Rationale, review),
            };
        }

        // Layer 5: nothing resolved it, so a human decides.
        return Ask(call, argsHash, tainted ? RiskLevel.Medium : RiskLevel.Low, "no rule matched");
    }

    private GateVerdict Ask(ToolCallContext call, string argsHash, RiskLevel risk, string reason, ReviewResult? review = null)
    {
        // FR-RTN-08: a dry run never prompts anyone; whatever would need a human is auto-denied.
        if (call.DryRun)
        {
            return GateVerdict.Deny(DryRunLayer, $"dry run: would require approval ({reason})");
        }

        var preview = $"{call.Agent.Id} → {call.Tool} {CanonicalJson.Serialize(call.Args)}";
        if (preview.Length > 2_000)
        {
            preview = string.Concat(preview.AsSpan(0, 2_000), "…");
        }

        var request = approvals.Request(
            call.Agent.Id,
            call.SessionId,
            call.Tool,
            argsHash,
            preview,
            risk,
            call.Origin,
            review?.Verdict.ToString(),
            review?.Rationale);

        return new GateVerdict(GateOutcome.PendingApproval, "human", reason, request);
    }
}
