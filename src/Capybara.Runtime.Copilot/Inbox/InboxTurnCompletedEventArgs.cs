using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Runtime.Copilot.Hosting;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// A message's turn completed and was acknowledged. <see cref="Steered"/> lists user DMs that were interjected into it
/// and acknowledged with it. A reply to a user DM (see <see cref="FleetMessage.Channel"/>) is delivered by the host.
/// </summary>
public sealed class InboxTurnCompletedEventArgs(AgentId agent, FleetMessage message, TurnResult result, IReadOnlyList<FleetMessage> steered) : EventArgs
{
    public AgentId Agent { get; } = agent;

    public FleetMessage Message { get; } = message;

    public TurnResult Result { get; } = result;

    public IReadOnlyList<FleetMessage> Steered { get; } = steered;
}
