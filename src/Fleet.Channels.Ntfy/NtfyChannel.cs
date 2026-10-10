using System.Net.Http.Headers;
using System.Text;

namespace Fleet.Channels.Ntfy;

/// <summary>Preferred self-hosted ntfy push adapter with nonce-bound action buttons (FR-CLI-03, NFR-06).</summary>
public sealed class NtfyChannel(HttpClient httpClient, NtfyOptions options) : INotificationChannel
{
    public string Name => "ntfy";

    public bool SupportsSensitiveContent => options.SupportsSensitiveContent;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var topicUri = new Uri(options.BaseUrl, options.Topic);
        using var request = new HttpRequestMessage(HttpMethod.Post, topicUri)
        {
            Content = new StringContent(notification.Body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("Title", notification.Title);
        request.Headers.TryAddWithoutValidation("Priority", ToNtfyPriority(notification.Priority));
        request.Headers.TryAddWithoutValidation("Tags", ToTags(notification));
        request.Headers.TryAddWithoutValidation("Click", notification.DeepLinkPath);
        request.Headers.TryAddWithoutValidation("X-Markdown", "no");
        if (notification.Actions.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Actions", string.Join(", ", notification.Actions.Select(ToActionHeader)));
        }

        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string ToActionHeader(NotificationAction action) => $"http, {Escape(action.Label)}, {action.CallbackPath}, method=POST, clear=true";

    private static string Escape(string value) => value.Replace(",", string.Empty, StringComparison.Ordinal);

    private static string ToTags(Notification notification) => notification.Kind switch
    {
        NotificationKind.Approval => "warning",
        NotificationKind.Alert => "rotating_light",
        NotificationKind.NeedsInput => "question",
        _ => "white_check_mark",
    };

    private static string ToNtfyPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Low => "2",
        NotificationPriority.High => "4",
        NotificationPriority.Critical => "5",
        _ => "3",
    };
}
