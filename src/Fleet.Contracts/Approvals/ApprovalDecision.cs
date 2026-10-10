namespace Fleet.Contracts.Approvals;

/// <summary>Human choices on an approval card (FR-SAF-04).</summary>
public enum ApprovalDecision
{
    AllowOnce,
    AlwaysAllow,
    Deny,
}
