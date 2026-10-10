using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Capybara.Client;

/// <summary>DI helpers for typed fleet clients (FR-CLI-02, FR-CLI-05).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCapybaraClient(this IServiceCollection services, Action<CapybaraClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddTransient(sp => new CapybaraAuthenticationHandler(sp.GetRequiredService<IOptions<CapybaraClientOptions>>().Value));
        services.AddHttpClient<CapybaraApiClient>((sp, client) => client.BaseAddress = sp.GetRequiredService<IOptions<CapybaraClientOptions>>().Value.BaseAddress)
            .AddHttpMessageHandler<CapybaraAuthenticationHandler>();
        return services;
    }
}
