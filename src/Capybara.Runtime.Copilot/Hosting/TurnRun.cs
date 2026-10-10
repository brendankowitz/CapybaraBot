using System.Runtime.ExceptionServices;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>What a turn needs to observe of its session's (root agent's) event stream.</summary>
internal enum TurnEventKind
{
    /// <summary><c>session.idle</c>: no run is active any more.</summary>
    Idle,

    /// <summary><c>user.message</c>, with the message id <c>send</c> returned: the message entered a run.</summary>
    UserMessage,

    /// <summary>An assistant message, with its text.</summary>
    Assistant,

    /// <summary><c>assistant.turn_start</c>: the agentic loop is running.</summary>
    RunActivity,
}

internal readonly record struct TurnEvent(TurnEventKind Kind, string? Content = null, string? MessageId = null);

/// <summary>The session operations a turn needs; the seam that lets run handling be tested without a CLI.</summary>
internal interface ITurnSession
{
    /// <summary>Subscribes to the root agent's run events, in the order the session emitted them; dispose to unsubscribe.</summary>
    IDisposable Subscribe(Action<TurnEvent> handler);

    /// <summary>Sends the prompt and waits for the run to finish; returns the final assistant message.</summary>
    Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Sends without waiting (steering); returns the id its <c>user.message</c> event will carry.</summary>
    Task<string> SendAsync(MessageOptions message, CancellationToken cancellationToken);

    /// <summary>Aborts the active run. On an idle session this does nothing and emits no idle.</summary>
    Task AbortAsync(CancellationToken cancellationToken);
}

/// <summary>Timing bounds for a <see cref="TurnRun"/>.</summary>
/// <param name="Timeout">Longest a run (the turn's own, or a late steer's) may take before it is aborted.</param>
/// <param name="AbortTimeout">
/// Longest an abort may take to reach idle, and steering in flight to complete; past either the session is discarded.
/// </param>
/// <param name="IdleGrace">
/// How long a failed send waits for the run's own idle before aborting (after <c>session.error</c> the SDK throws and the
/// run's idle follows within milliseconds; aborting an idle session would never signal idle).
/// </param>
/// <param name="LateRunGrace">How long to wait for a steering message's <c>user.message</c> event once the turn ends.</param>
/// <param name="Time">Clock for every wait.</param>
internal sealed record TurnTimings(TimeSpan Timeout, TimeSpan AbortTimeout, TimeSpan IdleGrace, TimeSpan LateRunGrace, TimeProvider Time);

/// <summary>
/// One turn on one session, so that no run outlives it. Subscribes to the session before sending, so it knows whether
/// the run has ended. If the send fails, times out or is cancelled, the run is stopped before the failure surfaces: if
/// it already went idle nothing more is done, otherwise it gets a short grace to end by itself and is then aborted and
/// its idle awaited. If even that fails, <see cref="TurnAbortFailedException"/> tells the caller to discard the session.
/// </summary>
/// <remarks>
/// <para>
/// Steering (FR-AGT-05) is admitted only while the run is active: it opens when the turn is sent and closes at the first
/// idle and when the send fails. Where a steering message landed is read from the session's own event order, never
/// from when its send returned (events are dispatched on their own consumer, so a send can return before or after the
/// idle that preceded it). Observed against the bundled CLI: a message steered into an active run, or queued behind its
/// last model call, emits its <c>user.message</c> before that run's single idle; one that reaches an idle session starts
/// a run of its own, its <c>user.message</c> after the idle. <see cref="FinishAsync"/> waits for each steering message's
/// <c>user.message</c> and, for one that started a run after idle number <c>k</c>, for idle <c>k + 1</c> (aborting the
/// run if it overruns), so nothing runs once the turn's context and locks are released.
/// </para>
/// <para>Events from sub-agents are not the turn's run and are never seen here. Thread-safe.</para>
/// </remarks>
internal sealed class TurnRun : IDisposable
{
    private readonly ITurnSession _session;
    private readonly TurnTimings _timings;
    private readonly bool _steerable;
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private readonly List<(Func<bool> Condition, TaskCompletionSource<bool> Signal)> _waiters = [];
    private readonly HashSet<string> _steers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _userMessages = new(StringComparer.Ordinal);
    private int _idles;
    private bool _running;
    private bool _open;
    private int _pendingSteers;
    private string? _lateContent;

    public TurnRun(ITurnSession session, bool steerable, TurnTimings timings)
    {
        _session = session;
        _timings = timings;
        _steerable = steerable;
        _subscription = session.Subscribe(OnEvent);
    }

    /// <summary>Steering messages that reached the session during this turn.</summary>
    public int Joined
    {
        get
        {
            lock (_gate)
            {
                return _steers.Count;
            }
        }
    }

    /// <summary>Sends the turn. Only cancellation by the caller, or the send's own failure, propagates — after the run stopped.</summary>
    /// <exception cref="TurnAbortFailedException">The send failed and the run could not be stopped.</exception>
    public async Task<string?> SendAsync(MessageOptions message, CancellationToken cancellationToken)
    {
        // Nothing has been sent yet, so nothing is running: no abort (which would wait for an idle that never comes).
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _open = _steerable && _idles == 0;
        }

        try
        {
            return await _session.SendAndWaitAsync(message, _timings.Timeout, cancellationToken);
        }
        catch (Exception sendFailure)
        {
            Close();
            try
            {
                await StopAsync(after: 0, _timings.IdleGrace, CancellationToken.None);
            }
            catch (Exception abortFailure)
            {
                throw new TurnAbortFailedException("The turn failed and its run could not be stopped.", sendFailure, abortFailure);
            }

            ExceptionDispatchInfo.Capture(sendFailure).Throw();
            throw;
        }
    }

    /// <summary>
    /// Interjects <paramref name="message"/> into the active run. <see langword="false"/> when steering is closed (the run
    /// ended or failed) or the send failed; the caller then handles the message as a turn of its own.
    /// </summary>
    /// <remarks>
    /// Cancellation only stops the caller waiting: the send itself goes on, because one abandoned after it reached the CLI
    /// would still start a run, and only the id it returns lets <see cref="FinishAsync"/> find and stop that run.
    /// </remarks>
    public async Task<bool> SteerAsync(MessageOptions message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_open)
            {
                return false;
            }

            _pendingSteers++;
        }

        return await DeliverAsync(message).WaitAsync(cancellationToken);
    }

    private async Task<bool> DeliverAsync(MessageOptions message)
    {
        string? id = null;
        try
        {
            id = await _session.SendAsync(message, CancellationToken.None);
        }
#pragma warning disable CA1031 // Any failure means not delivered (the session failed or is going away): not joined.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            List<TaskCompletionSource<bool>> release;
            lock (_gate)
            {
                if (id is not null)
                {
                    _steers.Add(id);
                }

                _pendingSteers--;
                release = Satisfied();
            }

            Release(release);
        }

        return id is not null;
    }

    /// <summary>
    /// Ends the turn: closes steering, waits for steering in flight, then for each steering message's <c>user.message</c>;
    /// a message that started a run of its own after the turn's run ended has that run awaited too — aborted past the
    /// timeout, or as soon as <paramref name="stop"/> fires (the kill switch, the turn's credit limit). <paramref name="stop"/>
    /// never shortens the waits that find such a run, only how long a run found may go on. Returns that run's
    /// final assistant text, if there was one.
    /// </summary>
    /// <exception cref="TurnAbortFailedException">Steering did not complete, or a late run could not be stopped.</exception>
    public async Task<string?> FinishAsync(CancellationToken stop = default)
    {
        Close();
        if (!await WaitUntilAsync(() => _pendingSteers == 0, _timings.AbortTimeout, CancellationToken.None))
        {
            throw new TurnAbortFailedException($"Steering into the turn did not complete within {_timings.AbortTimeout}.");
        }

        lock (_gate)
        {
            if (_steers.Count == 0)
            {
                return null;
            }
        }

        // Each steering message's user.message says which run it is in. One never seen within the grace was not taken
        // into a run (the CLI still holds it); then only a run already visible after the last idle is stopped. The grace
        // is not cut short by stop: it is how a late run is found at all, and one found is then aborted at once.
        await WaitUntilAsync(() => _steers.All(_userMessages.ContainsKey), _timings.LateRunGrace, CancellationToken.None);
        int after;
        lock (_gate)
        {
            after = _steers.Select(id => _userMessages.GetValueOrDefault(id)).Max();
            if (_running && _idles > after && !_steers.All(_userMessages.ContainsKey))
            {
                after = _idles;
            }
        }

        if (after == 0)
        {
            // Every steering message joined the turn's own run, which has ended.
            return null;
        }

        try
        {
            await StopAsync(after, _timings.Timeout, stop);
        }
        catch (Exception abortFailure)
        {
            throw new TurnAbortFailedException("A run started by a late steering message could not be stopped.", abortFailure);
        }

        lock (_gate)
        {
            return _lateContent;
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        List<TaskCompletionSource<bool>> pending;
        lock (_gate)
        {
            pending = [.. _waiters.Select(w => w.Signal)];
            _waiters.Clear();
        }

        foreach (var signal in pending)
        {
            signal.TrySetResult(false);
        }
    }

    private static void Release(List<TaskCompletionSource<bool>> signals)
    {
        foreach (var signal in signals)
        {
            signal.TrySetResult(true);
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            _open = false;
        }
    }

    /// <summary>
    /// Makes sure the run that follows idle number <paramref name="after"/> has ended: wait up to
    /// <paramref name="wait"/> (or until <paramref name="stop"/>) for its idle, else abort it and wait for the idle that
    /// confirms it.
    /// </summary>
    private async Task StopAsync(int after, TimeSpan wait, CancellationToken stop)
    {
        if (await WaitUntilAsync(() => _idles > after, wait, stop))
        {
            return;
        }

        await _session.AbortAsync(CancellationToken.None);
        if (!await WaitUntilAsync(() => _idles > after, _timings.AbortTimeout, CancellationToken.None))
        {
            throw new TimeoutException($"The session did not become idle within {_timings.AbortTimeout} of the abort.");
        }
    }

    /// <summary>
    /// True once <paramref name="condition"/> (evaluated under the lock, as events and steering change state) holds;
    /// false after <paramref name="timeout"/> or once <paramref name="stop"/> fires.
    /// </summary>
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken stop)
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (condition())
            {
                return true;
            }

            _waiters.Add((condition, signal));
        }

        using var timer = new CancellationTokenSource(timeout, _timings.Time);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, stop);
        using var registration = giveUp.Token.Register(() => signal.TrySetResult(false));
        var met = await signal.Task;
        lock (_gate)
        {
            _waiters.RemoveAll(w => w.Signal == signal);
        }

        return met;
    }

    /// <summary>Waiters whose condition now holds, removed from the list. Call under the lock.</summary>
    private List<TaskCompletionSource<bool>> Satisfied()
    {
        List<TaskCompletionSource<bool>> release = [];
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Condition())
            {
                release.Add(_waiters[i].Signal);
                _waiters.RemoveAt(i);
            }
        }

        return release;
    }

    private void OnEvent(TurnEvent e)
    {
        List<TaskCompletionSource<bool>> release;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case TurnEventKind.Idle:
                    _idles++;
                    _running = false;
                    _open = false;
                    break;

                case TurnEventKind.UserMessage:
                    _running = true;
                    if (e.MessageId is { } id)
                    {
                        _userMessages.TryAdd(id, _idles);
                    }

                    break;

                case TurnEventKind.Assistant:
                    _running = true;
                    if (_idles > 0)
                    {
                        _lateContent = e.Content;
                    }

                    break;

                default:
                    _running = true;
                    break;
            }

            release = Satisfied();
        }

        Release(release);
    }
}

/// <summary><see cref="ITurnSession"/> over a live <see cref="CopilotSession"/>.</summary>
internal sealed class CopilotTurnSession(CopilotSession session) : ITurnSession
{
    public IDisposable Subscribe(Action<TurnEvent> handler) =>
        session.On<SessionEvent>(e =>
        {
            if (Map(e) is { } turnEvent)
            {
                handler(turnEvent);
            }
        });

    /// <summary>
    /// The run events of the root agent (as <c>SendAndWaitAsync</c> filters them); everything else — sub-agents' events,
    /// and session-level notices the CLI emits while idle — is not evidence of the turn's run.
    /// </summary>
    public static TurnEvent? Map(SessionEvent e) => e switch
    {
        { AgentId.Length: > 0 } => null,
        SessionIdleEvent => new TurnEvent(TurnEventKind.Idle),
        UserMessageEvent message => new TurnEvent(TurnEventKind.UserMessage, MessageId: message.Data?.MessageId),
        AssistantMessageEvent message => new TurnEvent(TurnEventKind.Assistant, message.Data?.Content),
        AssistantTurnStartEvent => new TurnEvent(TurnEventKind.RunActivity),
        _ => null,
    };

    public async Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await session.SendAndWaitAsync(message, timeout, cancellationToken))?.Data?.Content;

    public Task<string> SendAsync(MessageOptions message, CancellationToken cancellationToken) => session.SendAsync(message, cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken) => session.AbortAsync(cancellationToken);
}

/// <summary>A turn failed and its run could not be stopped; the session must be discarded.</summary>
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

    /// <summary>Why stopping the run failed.</summary>
    public Exception? AbortFailure { get; }
}
