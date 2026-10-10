using Fleet.Contracts.Agents;

namespace Fleet.Channels;

/// <summary>Notification routing settings for quiet hours, focus expiry, and approval fatigue controls (FR-CLI-07, FR-SAF-10).</summary>
public sealed record ChannelOptions
{
    public TimeSpan PresenceFocusTtl { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan LowRiskBatchWindow { get; init; } = TimeSpan.FromSeconds(30);

    public QuietHoursOptions QuietHours { get; init; } = new();

    public bool CriticalBypassesQuietHours { get; init; } = true;

    public bool ApprovalBypassesQuietHours { get; init; } = true;

    public IReadOnlyDictionary<AgentId, bool> AgentNotificationToggles { get; init; } = new Dictionary<AgentId, bool>();
}
