using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Channels.Ntfy;

public static class CapybaraNtfyServiceCollectionExtensions
{
    private const string HttpClientName = "capybara-ntfy";

    /// <summary>Registers the ntfy adapter as an <see cref="INotificationChannel"/>. Register it before Telegram so it is preferred (FR-CLI-03).</summary>
    public static IServiceCollection AddCapybaraNtfyChannel(this IServiceCollection services, NtfyOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpClient(HttpClientName);
        services.AddSingleton<INotificationChannel>(sp =>
            new NtfyChannel(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName), options));
        return services;
    }
}
