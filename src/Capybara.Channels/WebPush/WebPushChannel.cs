using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels.WebPush;

/// <summary>Sends encrypted Web Push notifications; iOS delivery requires iOS 16.4+ and Home Screen installation (FR-CLI-06).</summary>
public sealed partial class WebPushChannel(
    HttpClient httpClient,
    WebPushSubscriptionStore subscriptions,
    WebPushVapidOptions vapidOptions,
    TimeProvider timeProvider,
    ILogger<WebPushChannel>? logger = null) : INotificationChannel
{
    private readonly ILogger<WebPushChannel> _logger = logger ?? NullLogger<WebPushChannel>.Instance;

    public string Name => "web-push";

    public bool SupportsSensitiveContent => true;

    /// <summary>
    /// Delivers to every subscription independently: a failing device is logged and skipped, and expired subscriptions
    /// (404/410) are pruned. Throws only when nothing was delivered (including when there are no subscriptions), so the
    /// router falls back to the next channel instead of treating a no-op as success.
    /// </summary>
    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var targets = subscriptions.Subscriptions;
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No web push subscriptions are registered.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            notification.Title,
            notification.Body,
            notification.DeepLinkPath,
            Actions = notification.Actions,
        });

        var delivered = 0;
        foreach (var subscription in targets)
        {
            try
            {
                var encrypted = WebPushCrypto.Encrypt(payload, subscription.P256Dh, subscription.AuthSecret);
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
                {
                    Content = new ByteArrayContent(encrypted.ToAes128GcmBody()),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.TryAddWithoutValidation("TTL", "60");
                request.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");
                request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={WebPushCrypto.CreateVapidJwt(subscription.Endpoint, vapidOptions, timeProvider)}, k={vapidOptions.PublicKey}");
                request.Headers.TryAddWithoutValidation("Urgency", notification.Priority >= NotificationPriority.High ? "high" : "normal");
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    subscriptions.Remove(subscription.DeviceId);
                    LogPruned(_logger, subscription.DeviceId);
                }
                else if (response.IsSuccessStatusCode)
                {
                    delivered++;
                }
                else
                {
                    LogDeviceFailed(_logger, subscription.DeviceId, $"HTTP {(int)response.StatusCode}", null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad device (network error, malformed keys) must not stop delivery to the others.
                LogDeviceFailed(_logger, subscription.DeviceId, ex.Message, ex);
            }
        }

        if (delivered == 0)
        {
            throw new HttpRequestException($"Web push delivery failed for all {targets.Count} subscription(s).");
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Web push to device {DeviceId} failed: {Reason}")]
    private static partial void LogDeviceFailed(ILogger logger, string deviceId, string reason, Exception? exception);

    [LoggerMessage(2, LogLevel.Information, "Pruned expired web push subscription for device {DeviceId}.")]
    private static partial void LogPruned(ILogger logger, string deviceId);
}
