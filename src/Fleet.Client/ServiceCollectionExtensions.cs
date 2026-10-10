using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fleet.Client;

/// <summary>DI helpers for typed fleet clients (FR-CLI-02, FR-CLI-05).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFleetClient(this IServiceCollection services, Action<FleetClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddTransient(sp => new FleetAuthenticationHandler(sp.GetRequiredService<IOptions<FleetClientOptions>>().Value));
        services.AddHttpClient<FleetApiClient>((sp, client) => client.BaseAddress = sp.GetRequiredService<IOptions<FleetClientOptions>>().Value.BaseAddress)
            .AddHttpMessageHandler<FleetAuthenticationHandler>();
        return services;
    }
}
