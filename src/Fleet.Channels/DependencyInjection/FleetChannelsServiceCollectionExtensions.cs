using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Channels.DependencyInjection;

/// <summary>Registers notification routing, presence, phone escalation, and web push stores for channel adapters (FR-CLI-03, FR-CLI-06, FR-A2A-07).</summary>
public static class FleetChannelsServiceCollectionExtensions
{
    public static IServiceCollection AddFleetChannels(this IServiceCollection services, ChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.TryAddSingleton<IPresenceTracker>(sp => new PresenceTracker(sp.GetRequiredService<TimeProvider>(), options.PresenceFocusTtl));
        services.TryAddSingleton<WebPushSubscriptionStore>();
        services.TryAddSingleton(sp => new NotificationRouter(
            sp.GetServices<INotificationChannel>(),
            sp.GetRequiredService<IPresenceTracker>(),
            sp.GetRequiredService<Fleet.Observability.Alerts.IAlertSink>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<NotificationRouter>.Instance));
        services.TryAddSingleton<IUserEscalation, PhoneUserEscalation>();
        return services;
    }
}
