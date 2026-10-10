using Fleet.Memory.Paths;

namespace Fleet.Memory.Pages;

public sealed record MemoryPage(MemoryPath Path, PageMetadata Metadata, string Body);
