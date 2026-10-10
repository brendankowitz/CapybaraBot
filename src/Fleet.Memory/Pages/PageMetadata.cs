using Fleet.Contracts.Messaging;

namespace Fleet.Memory.Pages;

/// <summary>Provenance frontmatter carried by every page (FR-MEM-04) plus bi-temporal validity (FR-MEM-09).</summary>
public sealed record PageMetadata
{
    public string Description { get; init; } = string.Empty;

    /// <summary>Agent, session, connection or URL the content came from.</summary>
    public string Source { get; init; } = "user";

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset Updated { get; init; }

    public DateTimeOffset? LastValidated { get; init; }

    public DateTimeOffset? LastAccessed { get; init; }

    public Taint Taint { get; init; }

    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>When set, the fact has been superseded rather than overwritten.</summary>
    public DateTimeOffset? ValidTo { get; init; }
}
