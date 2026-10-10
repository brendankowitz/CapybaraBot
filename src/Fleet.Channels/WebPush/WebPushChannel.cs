using System.Net.Http.Headers;
using System.Text.Json;

namespace Fleet.Channels.WebPush;

/// <summary>Sends encrypted Web Push notifications; iOS delivery requires iOS 16.4+ and Home Screen installation (FR-CLI-06).</summary>
public sealed class WebPushChannel(HttpClient httpClient, WebPushSubscriptionStore subscriptions, WebPushVapidOptions vapidOptions, TimeProvider timeProvider) : INotificationChannel
{
    public string Name => "web-push";

    public bool SupportsSensitiveContent => true;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            notification.Title,
            notification.Body,
            notification.DeepLinkPath,
            Actions = notification.Actions,
        });

        foreach (var subscription in subscriptions.Subscriptions)
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
            response.EnsureSuccessStatusCode();
        }
    }
}
