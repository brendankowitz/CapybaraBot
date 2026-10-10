using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels.DependencyInjection;

/// <summary>Registers notification routing, presence, phone escalation, and web push stores for channel adapters (FR-CLI-03, FR-CLI-06, FR-A2A-07).</summary>
public static class CapybaraChannelsServiceCollectionExtensions
{
    /// <param name="services">Service collection to extend.</param>
    /// <param name="options">Routing options; see <see cref="ChannelOptions.FromAgents"/>.</param>
    /// <param name="webPushSubscriptionsPath">JSON file that persists Web Push subscriptions across restarts; null keeps them in memory (FR-CLI-06).</param>
    public static IServiceCollection AddCapybaraChannels(this IServiceCollection services, ChannelOptions options, string? webPushSubscriptionsPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddSingleton<IPresenceTracker>(sp => new PresenceTracker(sp.GetRequiredService<TimeProvider>(), options.PresenceFocusTtl));
        services.TryAddSingleton(_ => new WebPushSubscriptionStore(webPushSubscriptionsPath));
        services.TryAddSingleton(sp => new NotificationRouter(
            sp.GetServices<INotificationChannel>(),
            sp.GetRequiredService<IPresenceTracker>(),
            sp.GetRequiredService<Capybara.Observability.Alerts.IAlertSink>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<NotificationRouter>>() ?? NullLogger<NotificationRouter>.Instance));
        services.TryAddSingleton<IUserEscalation, PhoneUserEscalation>();
        return services;
    }
}
