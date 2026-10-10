namespace Fleet.Gateway.Realtime;

/// <summary>Registers and maps fleet realtime services (FR-CLI-02).</summary>
public static class FleetRealtimeEndpointExtensions
{
    public const string HubPath = "/hubs/fleet";

    public static IServiceCollection AddFleetRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSignalR();
        services.AddSingleton<IFleetEvents, FleetEventBroadcaster>();
        services.AddSingleton<AgentActivityTracker>();
        return services;
    }

    public static IEndpointRouteBuilder MapFleetRealtime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHub<FleetHub>(HubPath);
        return endpoints;
    }
}
