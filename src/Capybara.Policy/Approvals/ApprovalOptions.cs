namespace Capybara.Policy.Approvals;

public sealed class ApprovalOptions
{
    /// <summary>Expiry for approvals that originate from routines, triggers or other agents (~10 minutes).</summary>
    public TimeSpan UnattendedTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Expiry for approvals raised while the user is interacting directly.</summary>
    public TimeSpan InteractiveTtl { get; set; } = TimeSpan.FromMinutes(30);
}
