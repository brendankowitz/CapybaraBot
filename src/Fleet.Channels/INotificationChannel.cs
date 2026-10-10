namespace Fleet.Channels;

/// <summary>Outbound push channel abstraction for ntfy, Telegram, and Web Push (FR-CLI-03, FR-CLI-06).</summary>
public interface INotificationChannel
{
    string Name { get; }

    bool SupportsSensitiveContent { get; }

    ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default);
}
