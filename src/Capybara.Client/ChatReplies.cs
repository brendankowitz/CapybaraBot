namespace Capybara.Client;

/// <summary>Finding the reply to a queued (Accepted) turn in the chat history.</summary>
public static class ChatReplies
{
    /// <summary>
    /// The assistant entries that follow the latest user entry whose content is <paramref name="prompt"/>. Matching is by
    /// content and order, not by timestamp, so client/server clock skew cannot hide or invent a reply.
    /// </summary>
    public static IReadOnlyList<ChatEntryDto> To(IReadOnlyList<ChatEntryDto> history, string prompt)
    {
        ArgumentNullException.ThrowIfNull(history);
        var sent = -1;
        for (var i = history.Count - 1; i >= 0 && sent < 0; i--)
        {
            sent = history[i].Role == "user" && history[i].Content == prompt ? i : -1;
        }

        return sent < 0
            ? []
            : [.. history.Skip(sent + 1).TakeWhile(e => e.Role != "user").Where(e => e.Role == "assistant")];
    }
}
