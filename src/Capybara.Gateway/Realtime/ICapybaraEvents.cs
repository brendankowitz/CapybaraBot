using Capybara.Client;

namespace Capybara.Gateway.Realtime;

/// <summary>
/// Broadcasts user-visible fleet changes to connected clients (FR-CLI-02, FR-OBS-02, FR-OBS-03). Payloads are the
/// client contract's DTOs, so the typed handlers in <c>CapybaraRealtimeClient</c> deserialize them.
/// </summary>
public interface ICapybaraEvents
{
    Task PublishAgentStatusChangedAsync(string agentId, AgentActivityState state, int unread, CancellationToken cancellationToken = default);

    Task PublishThreadEntryAsync(ThreadEntryDto entry, CancellationToken cancellationToken = default);

    Task PublishApprovalRequestedAsync(ApprovalDto approval, CancellationToken cancellationToken = default);

    Task PublishApprovalResolvedAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>An <c>ask_user</c> question is waiting for the user (FR-A2A-07).</summary>
    Task PublishQuestionRequestedAsync(QuestionDto question, CancellationToken cancellationToken = default);

    Task PublishAlertAsync(AlertDto alert, CancellationToken cancellationToken = default);

    Task PublishKillSwitchChangedAsync(bool engaged, CancellationToken cancellationToken = default);

    /// <summary>An agent proposed a change to its own config, persona, routines or skills (FR-AGT-06).</summary>
    Task PublishProposalAsync(ProposalDto proposal, CancellationToken cancellationToken = default);
}
