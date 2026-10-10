using Capybara.Memory.Abstractions;
using Capybara.Memory.Admin;
using Capybara.Memory.Consolidation;
using Capybara.Memory.Export;
using Capybara.Memory.Search;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Memory.DependencyInjection;

/// <summary>Dependency injection entry point for Capybara.Memory (FR-MEM-01..12).</summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the wiki (<see cref="WikiStore"/> with its FTS5 index, rebuilt on start), the <see cref="IMemoryStore"/>
    /// composite over any registered <see cref="IMemoryProvider"/>s, <see cref="MemoryAdmin"/>, <see cref="MemoryExporter"/>
    /// and the nightly <see cref="ConsolidationJob"/> hosted service. Consolidation only stages proposals; apply or discard
    /// them with <see cref="WikiStore.ResolveUserChangeAsync"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider"/> and <see cref="IMemoryVersioning"/> (default: git CLI on the memory root) are registered
    /// only when the host has not already supplied them. An <see cref="IConsolidationSummarizer"/> is used when registered.
    /// </remarks>
    public static IServiceCollection AddCapybaraMemory(this IServiceCollection services, MemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMemoryVersioning>(_ => new GitCliMemoryVersioning(Path.GetFullPath(options.Root)));
        services.AddSingleton(sp => new FtsMemoryIndex(sp.GetRequiredService<MemoryOptions>()));
        services.AddSingleton(sp => new WikiStore(
            sp.GetRequiredService<MemoryOptions>(),
            sp.GetRequiredService<IMemoryVersioning>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<FtsMemoryIndex>()));
        services.AddSingleton<CompositeMemoryStore>();
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<CompositeMemoryStore>());
        services.AddSingleton(sp => new MemoryAdmin(sp.GetRequiredService<WikiStore>(), sp.GetRequiredService<IMemoryVersioning>()));
        services.AddSingleton<MemoryExporter>();
        services.AddSingleton(sp => new ConsolidationJob(
            sp.GetRequiredService<WikiStore>(),
            sp.GetRequiredService<MemoryOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IConsolidationSummarizer>()));
        services.AddHostedService<MemoryIndexHostedService>();
        services.AddHostedService<ConsolidationHostedService>();
        return services;
    }
}
