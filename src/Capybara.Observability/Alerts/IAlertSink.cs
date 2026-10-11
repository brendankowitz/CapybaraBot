namespace Capybara.Observability.Alerts;

/// <summary>Destination for alerts: ntfy, Telegram, the PWA, or an in-memory buffer.</summary>
public interface IAlertSink
{
    ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default);
}
