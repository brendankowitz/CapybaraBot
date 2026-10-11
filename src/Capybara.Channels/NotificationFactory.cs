using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;

namespace Capybara.Channels;

/// <summary>
/// Builds the minimal Finished, Needs input and Approval notifications (FR-CLI-03). Payloads carry only agent ids, tool names
/// and ids: never task output, questions or approval previews (FR-SAF-07, NFR-06). Anything naming a tool or question is
/// flagged sensitive so the router reduces it to a content-free line for channels that cannot carry it (Telegram).
/// </summary>
public static class NotificationFactory
{
    /// <summary>
    /// Gateway path a notification button POSTs to. The gateway endpoint must settle it through the approval's own nonce
    /// check (single use, args-hash bound), never trust the query string alone.
    /// </summary>
    public static string ApprovalActionPath(string approvalId, string nonce, ApprovalDecision decision) =>
        $"/api/approvals/{Uri.EscapeDataString(approvalId)}/act?nonce={Uri.EscapeDataString(nonce)}&decision={DecisionToken(decision)}";

    public static Notification Finished(AgentId agent) => new(
        NotificationKind.Finished,
        agent,
        $"{agent} finished",
        string.Empty,
        NotificationPriority.Normal,
        AgentPath(agent));

    public static Notification NeedsInput(AgentId agent) => new(
        NotificationKind.NeedsInput,
        agent,
        $"input needed: {agent}",
        string.Empty,
        NotificationPriority.High,
        AgentPath(agent),
        IsSensitive: true);

    /// <summary>
    /// "approval needed: ea → email_send" with Allow once and Deny buttons bound to the approval's single-use nonce
    /// (FR-SAF-04). Low-risk approvals are Low priority so the router batches them (FR-SAF-10).
    /// </summary>
    public static Notification Approval(ApprovalRequest approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var priority = approval.Risk switch
        {
            RiskLevel.Low => NotificationPriority.Low,
            RiskLevel.Medium => NotificationPriority.Normal,
            _ => NotificationPriority.High,
        };

        return new Notification(
            NotificationKind.Approval,
            approval.Agent,
            $"approval needed: {approval.Agent} → {approval.Tool}",
            string.Empty,
            priority,
            "/approvals",
            [
                new NotificationAction("Allow once", ApprovalActionPath(approval.Id, approval.Nonce, ApprovalDecision.AllowOnce)),
                new NotificationAction("Deny", ApprovalActionPath(approval.Id, approval.Nonce, ApprovalDecision.Deny)),
            ],
            IsSensitive: true);
    }

    private static string AgentPath(AgentId agent) => $"/agents/{Uri.EscapeDataString(agent.Value)}";

    private static string DecisionToken(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.AllowOnce => "allow-once",
        ApprovalDecision.AlwaysAllow => "always-allow",
        ApprovalDecision.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
    };
}
