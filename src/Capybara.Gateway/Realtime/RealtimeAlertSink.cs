using Capybara.Observability.Alerts;

namespace Capybara.Gateway.Realtime;

/// <summary>Persists every alert, then shows it live in the PWA and TUI (FR-OBS-05, FR-CLI-02).</summary>
/// <remarks>The alert is durable before the broadcast; a failed broadcast never loses it.</remarks>
public sealed partial class RealtimeAlertSink(IAlertSink inner, ICapybaraEvents events, ILogger<RealtimeAlertSink> logger) : IAlertSink
{
    public async ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await inner.PublishAsync(alert, cancellationToken);
        try
        {
            await events.PublishAlertAsync(alert, cancellationToken);
        }
#pragma warning disable CA1031 // Clients reload alerts from the durable sink; a dropped broadcast is cosmetic.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogBroadcastFailed(logger, ex, alert.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcasting alert {Kind} failed")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception exception, string kind);
}
