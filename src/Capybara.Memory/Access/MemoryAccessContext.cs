using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Memory.Access;

/// <summary>Who is touching memory, and from which kind of session (FR-MEM-05, FR-MEM-12).</summary>
public sealed record MemoryAccessContext
{
    public required AgentId Agent { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Set when running inside a project; gates <c>/projects/{p}/</c>.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Consult sessions are read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Incognito sessions read no memory and write none (FR-MEM-12). Every store in this assembly honours the flag; the
    /// runtime should set it via <see cref="AsIncognito"/> and may additionally use <c>NullMemoryStore.Instance</c>.
    /// </summary>
    public bool Incognito { get; init; }

    /// <summary>This context with <see cref="Incognito"/> set.</summary>
    public MemoryAccessContext AsIncognito() => this with { Incognito = true };

    /// <summary>Whether this agent may read <c>/user/</c>. The research agent may not.</summary>
    public bool CanReadUser { get; init; } = true;

    public Taint SessionTaint { get; init; }
}
