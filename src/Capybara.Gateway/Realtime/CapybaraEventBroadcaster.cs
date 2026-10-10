using Microsoft.AspNetCore.SignalR;

namespace Capybara.Gateway.Realtime;

/// <summary>SignalR implementation of <see cref="ICapybaraEvents"/> (FR-CLI-02).</summary>
public sealed class CapybaraEventBroadcaster(IHubContext<CapybaraHub> hub) : ICapybaraEvents
{
    public Task PublishAgentStatusChangedAsync(string agentId, AgentActivityState state, int unread, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("AgentStatusChanged", agentId, ToWireState(state), unread, cancellationToken);

    public Task PublishThreadEntryAsync(object entry, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("ThreadEntry", entry, cancellationToken);

    public Task PublishApprovalRequestedAsync(object approval, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("ApprovalRequested", approval, cancellationToken);

    public Task PublishApprovalResolvedAsync(string id, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("ApprovalResolved", id, cancellationToken);

    public Task PublishAlertAsync(object alert, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("Alert", alert, cancellationToken);

    public Task PublishKillSwitchChangedAsync(bool engaged, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("KillSwitchChanged", engaged, cancellationToken);

    public Task PublishProposalAsync(object proposal, CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("ProposalCreated", proposal, cancellationToken);

    private static string ToWireState(AgentActivityState state) => state switch
    {
        AgentActivityState.Working => "working",
        AgentActivityState.NeedsAttention => "needsAttention",
        _ => "idle",
    };
}
