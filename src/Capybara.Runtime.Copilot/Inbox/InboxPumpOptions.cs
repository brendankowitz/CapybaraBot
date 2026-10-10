namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>Retry and back-off for the per-agent inbox pump (FR-AGT-05, NFR-02).</summary>
public sealed class InboxPumpOptions
{
    /// <summary>Failed turns per message before it is dead-lettered and the user alerted.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// First retry delay; it doubles per attempt up to <see cref="MaxRetryDelay"/>. With the defaults a message survives
    /// about ten minutes of failures, well past a supervised runtime restart (NFR-02).
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a paused or over-budget agent's inbox is looked at again.</summary>
    public TimeSpan BlockedPollInterval { get; set; } = TimeSpan.FromSeconds(15);
}
