using Capybara.Observability.Alerts;

namespace Capybara.Channels;

/// <summary>Decorates alert publishing with warning-or-higher phone notifications (FR-CLI-03, FR-OBS-05).</summary>
public sealed class NotifyingAlertSink(IAlertSink inner, NotificationRouter router) : IAlertSink
{
    public async ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await inner.PublishAsync(alert, cancellationToken);
        if (alert.Severity < AlertSeverity.Warning)
        {
            return;
        }

        var priority = alert.Severity == AlertSeverity.Critical ? NotificationPriority.Critical : NotificationPriority.High;
        var notification = new Notification(
            NotificationKind.Alert,
            null,
            $"alert: {alert.Kind}",
            alert.Message,
            priority,
            "/alerts",
            IsSensitive: false);
        await router.RouteAsync(notification, cancellationToken);
    }
}
