using System.Text.Json.Serialization;

namespace Capybara.Gateway.Realtime;

/// <summary>Registers and maps fleet realtime services (FR-CLI-02).</summary>
public static class CapybaraRealtimeEndpointExtensions
{
    public const string HubPath = "/hubs/capybara";

    public static IServiceCollection AddCapybaraRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Enums as strings, like the REST API, so hub payloads match the client DTOs.
        services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
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
