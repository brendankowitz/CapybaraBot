namespace Fleet.Gateway.Realtime;

/// <summary>Broadcasts user-visible fleet changes to connected clients (FR-CLI-02, FR-OBS-02, FR-OBS-03).</summary>
public interface IFleetEvents
{
    Task PublishAgentStatusChangedAsync(string agentId, AgentActivityState state, int unread, CancellationToken cancellationToken = default);

    Task PublishThreadEntryAsync(object entry, CancellationToken cancellationToken = default);

    Task PublishApprovalRequestedAsync(object approval, CancellationToken cancellationToken = default);

    Task PublishApprovalResolvedAsync(string id, CancellationToken cancellationToken = default);

    Task PublishAlertAsync(object alert, CancellationToken cancellationToken = default);

    Task PublishKillSwitchChangedAsync(bool engaged, CancellationToken cancellationToken = default);
}
