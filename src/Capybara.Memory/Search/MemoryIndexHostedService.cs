using Capybara.Memory.Store;
using Microsoft.Extensions.Hosting;

namespace Capybara.Memory.Search;

/// <summary>Rebuilds the FTS5 index from the wiki on host start, so search never serves a stale or missing index (FR-MEM-11).</summary>
public sealed class MemoryIndexHostedService(WikiStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.RebuildSearchIndexAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
