using System.Net.Http.Json;

namespace Capybara.Channels.Telegram;

/// <summary>
/// Telegram Bot API notification fallback (FR-CLI-03, FR-SAF-07, NFR-06). Plain text only (no parse mode, so nothing is
/// rendered as markup), link previews disabled, and no action buttons, so no nonce ever leaves the tailnet.
/// Sensitive notifications are reduced to their kind plus a deep link.
/// </summary>
public sealed class TelegramChannel(HttpClient httpClient, TelegramOptions options) : INotificationChannel
{
    public string Name => "telegram";

    public bool SupportsSensitiveContent => false;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var link = options.DeepLinkBaseUrl is { } baseUrl ? new Uri(baseUrl, notification.DeepLinkPath).AbsoluteUri : notification.DeepLinkPath;
        var text = notification.IsSensitive
            ? $"{notification.Kind} notification\n{link}"
            : string.Join('\n', new[] { notification.Title, notification.Body, link }.Where(line => line.Length > 0));
        var payload = new
        {
            chat_id = options.ChatId,
            text,
            disable_web_page_preview = true,
            link_preview_options = new { is_disabled = true },
        };
        using var response = await httpClient.PostAsJsonAsync($"bot{options.BotToken}/sendMessage", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
