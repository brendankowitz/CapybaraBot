using System.Text.Json;

namespace Capybara.Channels.Telegram;

/// <summary>Accepts inbound Telegram text only from the single allowlisted chat or user id (FR-CLI-04).</summary>
public sealed class TelegramUpdateHandler(ConversationalChannelGuard guard, IInboundMessageSink sink)
{
    public ValueTask HandleAsync(JsonDocument update, CancellationToken cancellationToken = default) =>
        HandleAsync(update.RootElement, cancellationToken);

    public async ValueTask HandleAsync(JsonElement root, CancellationToken cancellationToken = default)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var textElement))
        {
            return;
        }

        var userId = ExtractUserId(message);
        if (userId is null || !guard.IsAllowed("telegram", userId))
        {
            return;
        }

        if (!TryGetUpdateId(root, out var id))
        {
            throw new InvalidDataException("Telegram update has no numeric update_id.");
        }

        await sink.ReceiveAsync("telegram", userId, textElement.GetString() ?? string.Empty, $"telegram:{id}", cancellationToken);
    }

    /// <summary>Reads the update's <c>update_id</c>; false when the update is not an object or the id is not an int64.</summary>
    /// <remarks>
    /// Both kind checks are load-bearing: <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws unless the
    /// element is an object, and <see cref="JsonElement.TryGetInt64"/> throws unless it is a number.
    /// </remarks>
    internal static bool TryGetUpdateId(JsonElement update, out long updateId)
    {
        updateId = 0;
        return update.ValueKind == JsonValueKind.Object
            && update.TryGetProperty("update_id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out updateId);
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
