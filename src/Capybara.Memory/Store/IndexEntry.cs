using Capybara.Contracts.Messaging;

namespace Capybara.Memory.Store;

/// <summary>One line of the ranked table of contents injected every turn (FR-MEM-02).</summary>
public sealed record IndexEntry(string Path, string Description, DateTimeOffset Updated)
{
    /// <summary>Whether this index entry came from untrusted memory (FR-SAF-03).</summary>
    public Taint Taint { get; init; }
}
