namespace Capybara.Policy.Approvals;

public enum ApprovalOutcome
{
    Accepted,
    NotFound,
    InvalidNonce,
    Expired,
    AlreadyDecided,

    /// <summary>Only the human principal can satisfy an approval; agent content never constitutes consent (FR-A2A-08).</summary>
    NotHuman,

    /// <summary>"Always allow" is not offered for high-risk tools.</summary>
    AlwaysNotPermitted,
}
