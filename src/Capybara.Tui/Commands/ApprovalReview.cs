using Capybara.Client;

namespace Capybara.Tui.Commands;

/// <summary>What the TUI will do for <c>approvals decide &lt;id&gt; &lt;choice&gt;</c>: refuse, or show the review text and ask first.</summary>
/// <param name="Choice">The wire decision (<see cref="ApprovalDecisions"/>).</param>
/// <param name="Refusal">Why an allow is refused, or <see langword="null"/> when the decision may be sent.</param>
/// <param name="Review">Lines to print before the decision, already revealed (no raw control or bidi characters); empty for deny.</param>
/// <param name="NeedsConfirmation">Ask the user to confirm before sending.</param>
public sealed record ApprovalDecisionPlan(string Choice, string? Refusal, IReadOnlyList<string> Review, bool NeedsConfirmation);

/// <summary>
/// The TUI applies the same rules as the PWA card: an approval whose arguments were not seen in full, unambiguously and
/// visibly can only be denied, and an allow is only sent after the user has seen every argument, revealed.
/// </summary>
public static class ApprovalReview
{
    /// <exception cref="ArgumentException">The choice text is not allow-once, always or deny.</exception>
    public static ApprovalDecisionPlan Plan(ApprovalDto approval, string choiceText, bool assumeYes)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var choice = ApprovalChoice.Parse(choiceText);
        if (choice == ApprovalDecisions.Deny)
        {
            return new ApprovalDecisionPlan(choice, null, [], NeedsConfirmation: false);
        }

        var preview = ApprovalPreview.Parse(approval);
        if (!preview.CanApprove)
        {
            return new ApprovalDecisionPlan(choice, $"Refusing to allow: {preview.BlockReason} Only 'deny' is possible.", Describe(approval, preview), NeedsConfirmation: false);
        }

        return choice == ApprovalDecisions.AlwaysAllow && !approval.OffersAlwaysAllow
            ? new ApprovalDecisionPlan(choice, "Refusing: 'always' is not offered for this high-risk tool. Use allow-once or deny.", Describe(approval, preview), NeedsConfirmation: false)
            : new ApprovalDecisionPlan(choice, null, Describe(approval, preview), NeedsConfirmation: !assumeYes);
    }

    /// <summary>The whole request as inert text: nothing is dropped; invisible characters appear as ⟦U+XXXX⟧ tokens.</summary>
    public static IReadOnlyList<string> Describe(ApprovalDto approval, ApprovalPreview preview)
    {
        var r = ApprovalPreview.Reveal;
        var lines = new List<string> { $"{r(approval.Agent)} → {r(approval.Tool)}   risk: {r(approval.Risk)}" };
        if (!string.IsNullOrEmpty(approval.ReviewerVerdict))
        {
            lines.Add($"reviewer: {r(approval.ReviewerVerdict)}{(string.IsNullOrEmpty(approval.ReviewerRationale) ? string.Empty : " - " + r(approval.ReviewerRationale))}");
        }

        lines.AddRange(preview.Warnings);
        if (preview.Email is { } email)
        {
            lines.Add($"To: {ApprovalPreview.RevealNonAscii(email.To)}");
            if (!string.IsNullOrEmpty(email.Cc))
            {
                lines.Add($"Cc: {ApprovalPreview.RevealNonAscii(email.Cc)}");
            }

            lines.Add($"Subject: {r(email.Subject)}");
            lines.Add("Body:");
            lines.Add(r(email.BodyText));
            lines.AddRange(email.References.Select(reference => $"link/image (not loaded): {ApprovalPreview.RevealNonAscii(reference)}"));
            lines.AddRange(email.HiddenContent.Select(hidden => $"HIDDEN CONTENT: {r(hidden)}"));
        }

        if (preview.Diff is not null)
        {
            lines.Add("Diff:");
            lines.Add(r(preview.Diff));
        }

        lines.AddRange(preview.Args.Select(a => $"{ApprovalPreview.RevealNonAscii(a.Key)} = {r(a.Value)}"));
        lines.Add("Exact arguments as received:");
        lines.Add(r(preview.Exact));
        return lines;
    }
}
