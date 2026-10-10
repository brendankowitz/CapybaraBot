using System.Runtime.ExceptionServices;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>The two session operations a turn needs; the seam that lets the abort guarantee be tested without a CLI.</summary>
internal interface ITurnSession
{
    /// <summary>Sends the prompt and waits for the run to finish; returns the final assistant message.</summary>
    Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Aborts the run in progress and waits until the session reports idle.
    /// </summary>
    /// <exception cref="Exception">The abort failed, or no idle arrived within <paramref name="idleTimeout"/>.</exception>
    Task AbortAsync(TimeSpan idleTimeout);
}

/// <summary>
/// Sends one turn so that no run outlives it. The SDK's wait gives up on timeout or cancellation without stopping the
/// run, which would keep spending, act after its context (dry run, routine, lock) was released, and let the next send
/// complete on the orphan's idle event. So on any failure of the send the run is aborted and the session's idle awaited
/// before the failure surfaces; if even that fails, <see cref="TurnAbortFailedException"/> tells the caller to discard
/// the session.
/// </summary>
/// <remarks>
/// Results are not correlated by message id: the CLI merges a late steering message and the next send into one run that
/// carries the steering message's id, so correlation would wait forever. With no run ever left behind, the first idle
/// after a send belongs to that send.
/// </remarks>
internal static class TurnSender
{
    public static async Task<string?> SendAsync(ITurnSession session, MessageOptions message, TimeSpan timeout, TimeSpan abortTimeout, CancellationToken cancellationToken)
    {
        try
        {
            return await session.SendAndWaitAsync(message, timeout, cancellationToken);
        }
        catch (Exception sendFailure)
        {
            try
            {
                await session.AbortAsync(abortTimeout);
            }
            catch (Exception abortFailure)
            {
                throw new TurnAbortFailedException("The turn failed and its run could not be aborted.", sendFailure, abortFailure);
            }

            ExceptionDispatchInfo.Capture(sendFailure).Throw();
            throw;
        }
    }
}

/// <summary><see cref="ITurnSession"/> over a live <see cref="CopilotSession"/>.</summary>
internal sealed class CopilotTurnSession(CopilotSession session) : ITurnSession
{
    public async Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await session.SendAndWaitAsync(message, timeout, cancellationToken))?.Data?.Content;

    public async Task AbortAsync(TimeSpan idleTimeout)
    {
        // Subscribed before aborting, so the idle that ends the aborted run cannot be missed.
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.On<SessionIdleEvent>(_ => idle.TrySetResult());
        await session.AbortAsync(CancellationToken.None);
        await idle.Task.WaitAsync(idleTimeout);
    }
}

/// <summary>A turn failed and its run could not be aborted; the session must be discarded and rebuilt.</summary>
public sealed class TurnAbortFailedException : Exception
{
    public TurnAbortFailedException()
    {
    }

    public TurnAbortFailedException(string message)
        : base(message)
    {
    }

    public TurnAbortFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TurnAbortFailedException(string message, Exception sendFailure, Exception abortFailure)
        : base(message, sendFailure) => AbortFailure = abortFailure;

    /// <summary>Why the abort itself failed.</summary>
    public Exception? AbortFailure { get; }
}
