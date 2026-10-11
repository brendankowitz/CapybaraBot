using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Supervision;

public enum TurnRecoveryState
{
    /// <summary>The runtime cannot replay its event log, so nothing could be reconciled.</summary>
    Unavailable,

    /// <summary>The session has no user turn yet.</summary>
    NoTurn,

    /// <summary>The last turn finished with a final assistant message.</summary>
    Completed,

    /// <summary>The last turn was aborted, which is a deliberate stop, not a crash.</summary>
    Aborted,

    /// <summary>The last turn stopped mid-flight; its side effects are unknown until someone checks.</summary>
    Interrupted,
}

/// <summary>
/// What the event log says about the most recent turn of a main session (FR-AGT-04). Turns on one session are serial,
/// so only the last <c>user.message</c> can be in flight. <see cref="InteractionId"/> identifies that turn. <see cref="UnfinishedTools"/> lists tool calls that started
/// but never reported completion: they may or may not have taken effect and must be verified, not blindly retried.
/// </summary>
public sealed record TurnRecovery(TurnRecoveryState State, string? InteractionId, IReadOnlyList<string> UnfinishedTools)
{
    public static TurnRecovery Unavailable { get; } = new(TurnRecoveryState.Unavailable, null, []);

    /// <summary>The user must check something: the turn was cut off, or tool calls never reported completion.</summary>
    public bool NeedsAttention => State == TurnRecoveryState.Interrupted || UnfinishedTools.Count > 0;

#pragma warning disable GHCP001 // ParentToolCallId is deprecated but is the only way to tell sub-agent events from the root run in 1.0.19.
    public static TurnRecovery Analyze(IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var lastUser = -1;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is UserMessageEvent)
            {
                lastUser = i;
                break;
            }
        }

        if (lastUser < 0)
        {
            return new TurnRecovery(TurnRecoveryState.NoTurn, null, []);
        }

        // The interaction id identifies the turn across restarts; the event id stands in when the runtime assigned none.
        var interaction = ((UserMessageEvent)events[lastUser]).Data?.InteractionId ?? events[lastUser].Id.ToString("N");
        var openTurns = new HashSet<string>(StringComparer.Ordinal);
        var openTools = new Dictionary<string, string>(StringComparer.Ordinal);
        AssistantMessageData? finalMessage = null;
        var resultUnread = false;

        foreach (var e in events.Skip(lastUser + 1))
        {
            switch (e)
            {
                case AbortEvent:
                    return new TurnRecovery(TurnRecoveryState.Aborted, interaction, [.. openTools.Values.Order(StringComparer.Ordinal)]);

                case AssistantTurnStartEvent { Data.TurnId: { } turn }:
                    openTurns.Add(turn);
                    break;

                case AssistantTurnEndEvent { Data.TurnId: { } turn }:
                    openTurns.Remove(turn);
                    break;

                case ToolExecutionStartEvent { Data: { ToolCallId: { } call } start }:
                    openTools[call] = start.ToolName;
                    break;

                case ToolExecutionCompleteEvent { Data: { ToolCallId: { } call } complete }:
                    openTools.Remove(call);

                    // A top-level result the model has not answered yet means the loop was still running.
                    resultUnread |= complete.ParentToolCallId is null;
                    break;

                case AssistantMessageEvent { Data: { ParentToolCallId: null } message }:
                    finalMessage = message;
                    resultUnread = false;
                    break;
            }
        }

        var completed = openTurns.Count == 0
            && openTools.Count == 0
            && !resultUnread
            && finalMessage is not null
            && (finalMessage.ToolRequests is null || finalMessage.ToolRequests.Length == 0);

        return completed
            ? new TurnRecovery(TurnRecoveryState.Completed, interaction, [])
            : new TurnRecovery(TurnRecoveryState.Interrupted, interaction, [.. openTools.Values.Order(StringComparer.Ordinal)]);
    }
#pragma warning restore GHCP001
}
