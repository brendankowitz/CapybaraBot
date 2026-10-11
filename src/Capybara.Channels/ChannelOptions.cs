using Capybara.Contracts.Agents;

namespace Capybara.Channels;

/// <summary>Notification routing settings for quiet hours, focus expiry, and approval fatigue controls (FR-CLI-07, FR-SAF-10).</summary>
public sealed record ChannelOptions
{
    public TimeSpan PresenceFocusTtl { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan LowRiskBatchWindow { get; init; } = TimeSpan.FromSeconds(30);

    public QuietHoursOptions QuietHours { get; init; } = new();

    public bool CriticalBypassesQuietHours { get; init; } = true;

    public bool ApprovalBypassesQuietHours { get; init; } = true;

    /// <summary>Per-agent notification switch; agents that are not listed are enabled (FR-CLI-07).</summary>
    public IReadOnlyDictionary<AgentId, bool> AgentNotificationToggles { get; init; } = new Dictionary<AgentId, bool>();

    /// <summary>Builds options whose per-agent toggles mirror <see cref="AgentDefinition.Notifications"/> (FR-CLI-07).</summary>
    public static ChannelOptions FromAgents(IEnumerable<AgentDefinition> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return new ChannelOptions
        {
            AgentNotificationToggles = agents.ToDictionary(a => a.Id, a => a.Notifications),
        };
    }
}
