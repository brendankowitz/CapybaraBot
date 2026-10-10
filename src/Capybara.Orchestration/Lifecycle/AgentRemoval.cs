using Capybara.Contracts.Agents;
using Capybara.Contracts.Connections;

namespace Capybara.Orchestration.Lifecycle;

public enum AgentRemovalKind
{
    /// <summary>Config moved to <c>agents/_archive/</c>; runtime state is kept.</summary>
    Archived,

    /// <summary>Config removed; <c>{state}/agents/{id}</c> (workspace and <c>BaseDirectory</c>) is deleted after observers run.</summary>
    Deleted,
}

/// <summary>
/// An agent that has just left the live configuration (FR-AGT-07). <see cref="Grants"/> are the grants it held, for
/// the broker to revoke; <see cref="Routines"/> are the ids of the routines removed with it, for the scheduler to
/// unschedule.
/// </summary>
public sealed record AgentRemoval(
    AgentId Agent,
    AgentRemovalKind Kind,
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<string> Routines,
    string RemovedBy);
