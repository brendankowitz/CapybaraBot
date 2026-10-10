using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Channels.Ntfy;

/// <summary>
/// Preferred self-hosted ntfy push adapter (FR-CLI-03, NFR-06). Publishes as JSON so titles may contain any Unicode
/// (HTTP headers cannot); markdown is off and no attachments are sent, so nothing is rendered or fetched remotely (FR-SAF-07).
/// A notification with actions (an approval) gets one <c>view</c> button opening its deep link in the PWA, where the device
/// token is: the phone's ntfy app holds no gateway credential, so a button POSTing to the gateway could only be rejected,
/// and the approval's single-use nonce never leaves the gateway for the ntfy server (FR-CLI-03, FR-SAF-04).
/// </summary>
public sealed class NtfyChannel(HttpClient httpClient, NtfyOptions options) : INotificationChannel
{
    /// <summary>Label of the button that opens a notification's actions in the PWA.</summary>
    public const string ReviewLabel = "Review";

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "ntfy";

    public bool SupportsSensitiveContent => options.SupportsSensitiveContent;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var gateway = options.GatewayBaseUrl;
        var payload = new NtfyMessage(
            options.Topic,
            notification.Title,
            notification.Body,
            ToNtfyPriority(notification.Priority),
            [ToTag(notification.Kind)],
            gateway is null ? null : new Uri(gateway, notification.DeepLinkPath).AbsoluteUri,
            gateway is null || notification.Actions.Count == 0
                ? null
                : [new NtfyAction("view", ReviewLabel, new Uri(gateway, notification.DeepLinkPath).AbsoluteUri, Clear: true)]);

        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string ToTag(NotificationKind kind) => kind switch
    {
        NotificationKind.Approval => "warning",
        NotificationKind.Alert => "rotating_light",
        NotificationKind.NeedsInput => "question",
        _ => "white_check_mark",
    };

    private static int ToNtfyPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Low => 2,
        NotificationPriority.High => 4,
        NotificationPriority.Critical => 5,
        _ => 3,
    };

    // Property names follow ntfy's JSON publishing format (https://docs.ntfy.sh/publish/#publish-as-json).
    private sealed record NtfyMessage(
        [property: JsonPropertyName("topic")] string Topic,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("tags")] string[] Tags,
        [property: JsonPropertyName("click")] string? Click,
        [property: JsonPropertyName("actions")] NtfyAction[]? Actions,
        [property: JsonPropertyName("markdown")] bool Markdown = false);

    private sealed record NtfyAction(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("clear")] bool Clear);
}
