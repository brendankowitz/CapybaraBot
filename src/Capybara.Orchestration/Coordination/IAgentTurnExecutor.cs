using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Coordination;

/// <summary>Runs an ephemeral consult session on the target agent's own runtime. Implemented by the Copilot runtime.</summary>
public interface IAgentTurnExecutor
{
    Task<ConsultReply> ConsultAsync(AgentDefinition target, string sessionId, string question, Envelope envelope, CancellationToken cancellationToken);
}

/// <summary>The consulted agent's answer plus whether its session ingested untrusted content.</summary>
public sealed record ConsultReply(string Answer, Taint Taint, IReadOnlyList<Artifact> Artifacts);
