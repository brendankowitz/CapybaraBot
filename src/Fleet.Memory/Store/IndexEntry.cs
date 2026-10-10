namespace Fleet.Memory.Store;

/// <summary>One line of the ranked table of contents injected every turn (FR-MEM-02).</summary>
public sealed record IndexEntry(string Path, string Description, DateTimeOffset Updated);
