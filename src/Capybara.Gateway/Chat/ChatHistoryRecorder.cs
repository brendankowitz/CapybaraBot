using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Lifecycle;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Inbox;

namespace Capybara.Gateway.Chat;

/// <summary>
/// Writes each reply to a user's direct message into the <see cref="ChatHistory"/>, under the id of every message the
/// turn answered (FR-CLI-02). Agent, task and routine turns are not chat and are not recorded.
/// </summary>
/// <remarks>
/// The pump raises <see cref="InboxPumpHostedService.TurnCompleted"/> after the turn is persisted and acknowledged; the
/// line is appended synchronously so it is in the history before the pump takes the next message. A failed write is
/// logged and never reaches the pump.
/// </remarks>
public sealed partial class ChatHistoryRecorder(InboxPumpHostedService pumps, ChatHistory chat, TimeProvider time, ILogger<ChatHistoryRecorder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        pumps.TurnCompleted += OnTurnCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        pumps.TurnCompleted -= OnTurnCompleted;
        return Task.CompletedTask;
    }

    /// <summary>What the chat shows as the reply: the content, or why there is none, so a reply is never a blank line.</summary>
    internal static string ReplyText(TurnResult result) => result switch
    {
        { Content: { Length: > 0 } content } => content,
        { Success: true } => "(no reply)",
        _ => $"{result.Status}: {result.Error ?? "the turn did not complete."}",
    };

    private void OnTurnCompleted(object? sender, InboxTurnCompletedEventArgs e)
    {
        var incognito = SessionIds.TryParseIncognito(e.Message.SessionId, out _, out _);
        if (e.Message.Origin != MessageOrigin.User || (e.Message.SessionId is not null && !incognito))
        {
            return;
        }

        try
        {
            chat.RecordReply(e.Agent, e.Message, e.Steered, ReplyText(e.Result), incognito, time.GetUtcNow());
        }
#pragma warning disable CA1031 // The reply is already persisted in the session; a lost chat line must not fail the pump.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecordFailed(logger, ex, e.Agent.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the chat reply of {Agent}")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string agent);
}

/// <summary>A deleted agent's chat is removed with it; archiving keeps it (FR-AGT-07).</summary>
public sealed class ClearChatHistoryObserver(ChatHistory chat) : IAgentLifecycleObserver
{
    public ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        if (removal.Kind == AgentRemovalKind.Deleted)
        {
            chat.Delete(removal.Agent);
        }

        return ValueTask.CompletedTask;
    }
}
