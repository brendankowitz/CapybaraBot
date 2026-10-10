namespace Capybara.Gateway.Realtime;

/// <summary>Registers and maps fleet realtime services (FR-CLI-02).</summary>
public static class CapybaraRealtimeEndpointExtensions
{
    public const string HubPath = "/hubs/capybara";

    public static IServiceCollection AddCapybaraRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSignalR();
        services.AddSingleton<ICapybaraEvents, CapybaraEventBroadcaster>();
        services.AddSingleton<AgentActivityTracker>();
        return services;
    }

    public static IEndpointRouteBuilder MapCapybaraRealtime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<CapybaraHub>(HubPath);
        return endpoints;
    }
}
