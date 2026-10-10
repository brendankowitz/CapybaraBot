using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Capybara.Channels.WebPush;

/// <summary>Sends encrypted Web Push notifications; iOS delivery requires iOS 16.4+ and Home Screen installation (FR-CLI-06).</summary>
public sealed class WebPushChannel(HttpClient httpClient, WebPushSubscriptionStore subscriptions, WebPushVapidOptions vapidOptions, TimeProvider timeProvider) : INotificationChannel
{
    public string Name => "web-push";

    public bool SupportsSensitiveContent => true;

    /// <summary>
    /// Delivers to every subscription. Expired subscriptions (404/410) are pruned. Throws when there is nothing to deliver to
    /// or any delivery failed, so the router falls back to the next channel instead of treating a no-op as success.
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

        var failures = 0;
        foreach (var subscription in targets)
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
                failures++;
            }
            else if (!response.IsSuccessStatusCode)
            {
                failures++;
            }
        }

        if (failures == targets.Count)
        {
            throw new HttpRequestException($"Web push delivery failed for all {failures} subscription(s).");
        }
    }
}
