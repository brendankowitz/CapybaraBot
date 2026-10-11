using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>Runs consults on the target agent's own runtime, with its own tools and grants (FR-A2A-02).</summary>
public sealed class CopilotTurnExecutor(AgentRuntimePool pool) : IAgentTurnExecutor
{
    public Task<ConsultReply> ConsultAsync(AgentDefinition target, string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return pool.Get(target.Id).ConsultAsync(sessionId, question, envelope, cancellationToken);
    }
}
