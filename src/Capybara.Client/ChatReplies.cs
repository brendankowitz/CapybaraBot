namespace Capybara.Client;

/// <summary>Finding the reply to a queued (Accepted) turn in the chat history.</summary>
public static class ChatReplies
{
    /// <summary>
    /// The reply to the turn the server accepted: with <paramref name="messageId"/>, exactly the assistant entries that
    /// answer it (<see cref="ChatEntryDto.InReplyTo"/> or <see cref="ChatEntryDto.AlsoInReplyTo"/>), in order (a reply can
    /// have several parts). Nothing else is ever
    /// returned, so a repeated prompt, an agent-initiated message or a normalized prompt cannot attach the wrong reply.
    /// Without an id (a server that does not supply ids), see <see cref="AfterSnapshot"/>.
    /// </summary>
    public static IReadOnlyList<ChatEntryDto> To(IReadOnlyList<ChatEntryDto> history, string messageId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return [.. history.Where(e => e.Answers(messageId))];
    }

    /// <summary>
    /// Fallback when no message id is available. <paramref name="last"/> is the final history entry captured
    /// <b>before</b> sending (null if the history was empty). Only entries after it are considered; our message is the
    /// first user entry among them; the reply is the assistant entries right after that user entry. If that user entry
    /// carries a <see cref="ChatEntryDto.MessageId"/> the lookup upgrades to <see cref="To"/>. An agent-initiated entry
    /// before our message is ignored; one interleaved after it cannot be told apart without ids (hence the contract
    /// requires ids). Returns nothing if the snapshot entry is no longer in the window.
    /// </summary>
    public static IReadOnlyList<ChatEntryDto> AfterSnapshot(IReadOnlyList<ChatEntryDto> history, ChatEntryDto? last)
    {
        ArgumentNullException.ThrowIfNull(history);
        var start = 0;
        if (last is not null)
        {
            var index = -1;
            for (var i = history.Count - 1; i >= 0 && index < 0; i--)
            {
                // Field-wise, but AlsoInReplyTo is a list, which record equality compares by reference.
                index = history[i] with { AlsoInReplyTo = null } == last with { AlsoInReplyTo = null } ? i : -1;
            }

            if (index < 0)
            {
                return [];
            }

            start = index + 1;
        }

        var after = history.Skip(start).ToList();
        var ours = after.FindIndex(e => e.Role == "user");
        if (ours < 0)
        {
            return [];
        }

        return after[ours].MessageId is { Length: > 0 } id
            ? To(history, id)
            : [.. after.Skip(ours + 1).TakeWhile(e => e.Role != "user").Where(e => e.Role == "assistant")];
    }

    /// <summary>Id correlation when <paramref name="messageId"/> is known, else <see cref="AfterSnapshot"/>.</summary>
    public static IReadOnlyList<ChatEntryDto> Find(IReadOnlyList<ChatEntryDto> history, string? messageId, ChatEntryDto? snapshotLast) =>
        string.IsNullOrEmpty(messageId) ? AfterSnapshot(history, snapshotLast) : To(history, messageId);
}
