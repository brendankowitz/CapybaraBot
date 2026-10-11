using Capybara.Contracts.Agents;

namespace Capybara.Channels;

/// <summary>Content-minimal notification payload for phone and PWA channels (FR-CLI-03, FR-SAF-07, NFR-06).</summary>
public sealed record Notification(
    NotificationKind Kind,
    AgentId? Agent,
    string Title,
    string Body,
    NotificationPriority Priority,
    string DeepLinkPath,
    IReadOnlyList<NotificationAction>? Actions = null,
    bool IsSensitive = false)
{
    public IReadOnlyList<NotificationAction> Actions { get; init; } = Actions ?? [];
}
