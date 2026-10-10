using System.Net.Http.Json;

namespace Capybara.Channels.Telegram;

/// <summary>Telegram Bot API notification fallback with web previews disabled and no sensitive content (FR-CLI-03, FR-SAF-07, NFR-06).</summary>
public sealed class TelegramChannel(HttpClient httpClient, TelegramOptions options) : INotificationChannel
{
    public string Name => "telegram";

    public bool SupportsSensitiveContent => false;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var text = notification.IsSensitive
            ? $"{notification.Kind} notification\n{notification.DeepLinkPath}"
            : $"{notification.Title}\n{notification.Body}\n{notification.DeepLinkPath}";
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
