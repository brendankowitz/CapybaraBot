using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// Signals the runtimes raise for the host: turn start and end for the clients' Working / Needs attention states
/// (FR-CLI-02), and context compactions and denied outbound calls for the storm and spike alerts (FR-OBS-05).
/// </summary>
/// <remarks>
/// Events are raised synchronously on the runtime's (or the SDK's event) thread, so subscribers must return quickly and
/// hand any I/O off. A throwing subscriber is logged and never affects the turn or the other subscribers. The runtime is
/// the only production raiser; the raise methods are public so hosts can be exercised without a CLI.
/// </remarks>
public sealed partial class RuntimeActivity(ILogger<RuntimeActivity> logger)
{
    public event EventHandler<TurnActivityEventArgs>? TurnStarted;

    /// <summary>Raised when a turn ends; <see cref="TurnActivityEventArgs.Result"/> is null when the caller cancelled it.</summary>
    public event EventHandler<TurnActivityEventArgs>? TurnEnded;

    /// <summary>An agent's session finished compacting its context.</summary>
    public event EventHandler<AgentId>? Compacted;

    /// <summary>The policy refused a tool call that would have reached a URL.</summary>
    public event EventHandler<AgentId>? EgressDenied;

    public void OnTurnStarted(AgentId agent, SessionKind kind) => Raise(TurnStarted, new TurnActivityEventArgs(agent, kind, null), nameof(TurnStarted));

    public void OnTurnEnded(AgentId agent, SessionKind kind, TurnResult? result) => Raise(TurnEnded, new TurnActivityEventArgs(agent, kind, result), nameof(TurnEnded));

    public void OnCompacted(AgentId agent) => Raise(Compacted, agent, nameof(Compacted));

    public void OnEgressDenied(AgentId agent) => Raise(EgressDenied, agent, nameof(EgressDenied));

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, args);
            }
#pragma warning disable CA1031 // A subscriber must never fail a turn or starve the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(logger, ex, name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A {Event} subscriber failed")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception, string @event);
}

/// <summary>A turn of <see cref="Agent"/> in a session of <see cref="Kind"/> started or ended.</summary>
public sealed class TurnActivityEventArgs(AgentId agent, SessionKind kind, TurnResult? result) : EventArgs
{
    public AgentId Agent { get; } = agent;

    public SessionKind Kind { get; } = kind;

    /// <summary>The outcome; <see langword="null"/> at start, or when the caller cancelled the turn.</summary>
    public TurnResult? Result { get; } = result;
}
