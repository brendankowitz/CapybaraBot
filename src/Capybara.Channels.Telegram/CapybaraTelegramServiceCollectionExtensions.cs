using Capybara.Observability.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capybara.Channels.Telegram;

public static class CapybaraTelegramServiceCollectionExtensions
{
    private const string HttpClientName = "capybara-telegram";

    /// <summary>
    /// Registers the Telegram notification channel and, when <see cref="TelegramOptions.AllowedUserId"/> is set, the
    /// long-polling inbound service. The host must register an <see cref="IInboundMessageSink"/> (FR-CLI-04).
    /// </summary>
    public static IServiceCollection AddCapybaraTelegramChannel(this IServiceCollection services, TelegramOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // The HTTP timeout must outlast the server-side long poll. The bot token is part of every request URL, so the
        // default HttpClient logging (which records the full URI) is removed.
        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = options.ApiBaseUrl;
            client.Timeout = options.PollTimeout + TimeSpan.FromSeconds(30);
        }).RemoveAllLoggers();
        services.AddSingleton<INotificationChannel>(sp =>
            new TelegramChannel(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName), options));

        if (options.AllowedUserId is { Length: > 0 } allowedUserId)
        {
            services.AddHostedService(sp => new TelegramPollingService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                options,
                new TelegramUpdateHandler(
                    new ConversationalChannelGuard(
                        new Dictionary<string, string> { ["telegram"] = allowedUserId },
                        sp.GetService<ILogger<ConversationalChannelGuard>>()),
                    sp.GetRequiredService<IInboundMessageSink>()),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<TelegramPollingService>>(),
                sp.GetService<IAlertSink>()));
        }

        return services;
    }
}
