using System.Text.Json;

namespace Fleet.Channels.Telegram;

/// <summary>Accepts inbound Telegram text only from the single allowlisted chat or user id (FR-CLI-04).</summary>
public sealed class TelegramUpdateHandler(ConversationalChannelGuard guard, IInboundMessageSink sink)
{
    public async ValueTask HandleAsync(JsonDocument update, CancellationToken cancellationToken = default)
    {
        var root = update.RootElement;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var textElement))
        {
            return;
        }

        var userId = ExtractUserId(message);
        if (userId is null || !guard.IsAllowed("telegram", userId))
        {
            return;
        }

        await sink.ReceiveAsync("telegram", userId, textElement.GetString() ?? string.Empty, cancellationToken);
    }

    private static string? ExtractUserId(JsonElement message)
    {
        if (message.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fromId))
        {
            return fromId.GetRawText();
        }

        if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatId))
        {
            return chatId.GetRawText();
        }

        return null;
    }
}
