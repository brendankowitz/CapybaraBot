using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;

namespace Fleet.Memory.Access;

/// <summary>Who is touching memory, and from which kind of session (FR-MEM-05, FR-MEM-12).</summary>
public sealed record MemoryAccessContext
{
    public required AgentId Agent { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Set when running inside a project; gates <c>/projects/{p}/</c>.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Consult sessions are read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Incognito sessions read no memory and write none.</summary>
    public bool Incognito { get; init; }

    /// <summary>Whether this agent may read <c>/user/</c>. The research agent may not.</summary>
    public bool CanReadUser { get; init; } = true;

    public Taint SessionTaint { get; init; }
}
