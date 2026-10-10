using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Inbox;

/// <summary>
/// Per-agent durable FIFO (FR-AGT-05). A message is persisted before <see cref="EnqueueAsync"/> returns and stays until
/// acknowledged, so nothing is lost across a crash (NFR-02).
/// </summary>
public interface IInboxStore
{
    event EventHandler<AgentId>? MessageAvailable;

    ValueTask EnqueueAsync(FleetMessage message, CancellationToken cancellationToken = default);

    ValueTask<FleetMessage?> PeekAsync(AgentId agent, CancellationToken cancellationToken = default);

    ValueTask AcknowledgeAsync(AgentId agent, string messageId, CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(AgentId agent, CancellationToken cancellationToken = default);
}
