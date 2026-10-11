using Capybara.Contracts.Approvals;

namespace Capybara.Policy.Gate;

public enum GateOutcome
{
    Allow,
    Deny,

    /// <summary>Waiting on a human; the call is denied now and may be retried once approved.</summary>
    PendingApproval,
}

/// <summary>The decision of the layered gate and which layer produced it.</summary>
public sealed record GateVerdict(GateOutcome Outcome, string Layer, string Reason, ApprovalRequest? Approval = null)
{
    public static GateVerdict Allow(string layer, string reason) => new(GateOutcome.Allow, layer, reason);

    public static GateVerdict Deny(string layer, string reason) => new(GateOutcome.Deny, layer, reason);
}
