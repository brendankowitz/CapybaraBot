namespace Capybara.Memory.Admin;

/// <summary>A wiki page as listed for human administration (FR-MEM-12): path, size on disk and last write.</summary>
public sealed record MemoryPageInfo(string Path, long Size, DateTimeOffset UpdatedAt);
