using Capybara.Memory.Paths;

namespace Capybara.Memory.Pages;

public sealed record MemoryPage(MemoryPath Path, PageMetadata Metadata, string Body);
