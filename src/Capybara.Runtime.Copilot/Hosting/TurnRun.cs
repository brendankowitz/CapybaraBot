using System.Runtime.ExceptionServices;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>What a turn needs to observe of its session's event stream.</summary>
internal enum TurnEventKind
{
    /// <summary><c>session.idle</c>: no run is active any more.</summary>
    Idle,

    /// <summary>An assistant message, with its text.</summary>
    Assistant,

    /// <summary>Anything else (tool calls, usage, ...): evidence that a run is active.</summary>
    Other,
}

internal readonly record struct TurnEvent(TurnEventKind Kind, string? Content = null);

/// <summary>The session operations a turn needs; the seam that lets run handling be tested without a CLI.</summary>
internal interface ITurnSession
{
    /// <summary>Subscribes to the session's events; dispose to unsubscribe.</summary>
    IDisposable Subscribe(Action<TurnEvent> handler);

    /// <summary>Sends the prompt and waits for the run to finish; returns the final assistant message.</summary>
    Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Sends without waiting (steering).</summary>
    Task SendAsync(MessageOptions message, CancellationToken cancellationToken);

    /// <summary>Aborts the active run. On an idle session this does nothing and emits no idle.</summary>
    Task AbortAsync(CancellationToken cancellationToken);
}

/// <summary>Timing bounds for a <see cref="TurnRun"/>.</summary>
/// <param name="Timeout">Longest a run (the turn's own, or a late steer's) may take before it is aborted.</param>
/// <param name="AbortTimeout">Longest an abort may take to reach idle; past it the session is discarded.</param>
/// <param name="IdleGrace">
/// How long a failed send waits for the run's own idle before aborting (after <c>session.error</c> the SDK throws and the
/// run's idle follows within milliseconds; aborting an idle session would never signal idle).
/// </param>
/// <param name="LateRunGrace">How long to look for a run started by a steering message that arrived after idle.</param>
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
/// Steering (FR-AGT-05) is admitted only while the run is active: it closes at the first idle and when the send fails.
/// A steering message whose send completes after idle may have started a run of its own in the CLI;
/// <see cref="FinishAsync"/> waits for steering in flight and for any such run (aborting it if it overruns), so nothing
/// runs once the turn's context and locks are released.
/// </para>
/// <para>
/// Results are not correlated by message id: the CLI merges a late steering message and the next send into one run that
/// carries the steering message's id, so correlation would wait forever. With no run ever left behind, the first idle
/// after a send belongs to that send. Thread-safe.
/// </para>
/// </remarks>
internal sealed class TurnRun : IDisposable
{
    private readonly ITurnSession _session;
    private readonly TurnTimings _timings;
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private readonly List<(int After, TaskCompletionSource Signal)> _idleWaiters = [];
    private readonly TaskCompletionSource _activityAfterIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _drained = Completed();
    private int _idles;
    private bool _open;
    private int _pendingSteers;
    private int _joined;
    private int _late;
    private string? _lateContent;

    public TurnRun(ITurnSession session, bool steerable, TurnTimings timings)
    {
        _session = session;
        _timings = timings;
        _open = steerable;
        _subscription = session.Subscribe(OnEvent);
    }

    /// <summary>Steering messages that reached the session during this turn.</summary>
    public int Joined
    {
        get
        {
            lock (_gate)
            {
                return _joined;
            }
        }
    }

    /// <summary>Sends the turn. Only cancellation by the caller, or the send's own failure, propagates — after the run stopped.</summary>
    /// <exception cref="TurnAbortFailedException">The send failed and the run could not be stopped.</exception>
    public async Task<string?> SendAsync(MessageOptions message, CancellationToken cancellationToken)
    {
        // Nothing has been sent yet, so nothing is running: no abort (which would wait for an idle that never comes).
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await _session.SendAndWaitAsync(message, _timings.Timeout, cancellationToken);
        }
        catch (Exception sendFailure)
        {
            Close();
            try
            {
                await StopAsync(after: 0, _timings.IdleGrace);
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
    public async Task<bool> SteerAsync(MessageOptions message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_open)
            {
                return false;
            }

            if (_pendingSteers++ == 0)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var sent = false;
        try
        {
            await _session.SendAsync(message, cancellationToken);
            sent = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not delivered (the session failed or is going away): not joined.
        }
        finally
        {
            lock (_gate)
            {
                if (sent)
                {
                    _joined++;

                    // Accepted after the run ended: the CLI may have started a run for it.
                    if (_idles > 0)
                    {
                        _late++;
                    }
                }

                if (--_pendingSteers == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }

        return sent;
    }

    /// <summary>
    /// Ends the turn: closes steering, waits for steering in flight, and if a steering message arrived after the run ended
    /// and started a run of its own, waits for that run too (aborting it past the timeout). Returns that run's final
    /// assistant text, if there was one.
    /// </summary>
    /// <exception cref="TurnAbortFailedException">A late run could not be stopped.</exception>
    public async Task<string?> FinishAsync()
    {
        Close();
        Task drained;
        lock (_gate)
        {
            drained = _drained.Task;
        }

        await drained;
        lock (_gate)
        {
            if (_late == 0)
            {
                return null;
            }
        }

        // A run of its own shows itself with events after the first idle; a steer the CLI merged shows nothing.
        var started = await Task.WhenAny(_activityAfterIdle.Task, Task.Delay(_timings.LateRunGrace, _timings.Time)) == _activityAfterIdle.Task;
        if (!started)
        {
            return null;
        }

        try
        {
            await StopAsync(after: 1, _timings.Timeout);
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
        lock (_gate)
        {
            foreach (var (_, signal) in _idleWaiters)
            {
                signal.TrySetCanceled();
            }

            _idleWaiters.Clear();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
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
    /// <paramref name="wait"/> for its idle, else abort it and wait for the idle that confirms it.
    /// </summary>
    private async Task StopAsync(int after, TimeSpan wait)
    {
        if (await WaitForIdleAsync(after, wait))
        {
            return;
        }

        await _session.AbortAsync(CancellationToken.None);
        if (!await WaitForIdleAsync(after, _timings.AbortTimeout))
        {
            throw new TimeoutException($"The session did not become idle within {_timings.AbortTimeout} of the abort.");
        }
    }

    /// <summary>True once more than <paramref name="after"/> idles were seen, within <paramref name="timeout"/>.</summary>
    private async Task<bool> WaitForIdleAsync(int after, TimeSpan timeout)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_idles > after)
            {
                return true;
            }

            _idleWaiters.Add((after, signal));
        }

        var arrived = await Task.WhenAny(signal.Task, Task.Delay(timeout, _timings.Time)) == signal.Task;
        if (!arrived)
        {
            lock (_gate)
            {
                _idleWaiters.Remove((after, signal));
            }
        }

        return arrived && signal.Task.IsCompletedSuccessfully;
    }

    private void OnEvent(TurnEvent e)
    {
        List<TaskCompletionSource> release = [];
        lock (_gate)
        {
            if (_idles > 0)
            {
                _activityAfterIdle.TrySetResult();
            }

            switch (e.Kind)
            {
                case TurnEventKind.Idle:
                    _idles++;
                    _open = false;
                    foreach (var waiter in _idleWaiters.Where(w => _idles > w.After).ToList())
                    {
                        _idleWaiters.Remove(waiter);
                        release.Add(waiter.Signal);
                    }

                    break;

                case TurnEventKind.Assistant when _idles > 0:
                    _lateContent = e.Content;
                    break;
            }
        }

        foreach (var signal in release)
        {
            signal.TrySetResult();
        }
    }
}

/// <summary><see cref="ITurnSession"/> over a live <see cref="CopilotSession"/>.</summary>
internal sealed class CopilotTurnSession(CopilotSession session) : ITurnSession
{
    public IDisposable Subscribe(Action<TurnEvent> handler) =>
        session.On<SessionEvent>(e => handler(e switch
        {
            SessionIdleEvent => new TurnEvent(TurnEventKind.Idle),
            AssistantMessageEvent message => new TurnEvent(TurnEventKind.Assistant, message.Data?.Content),
            _ => new TurnEvent(TurnEventKind.Other),
        }));

    public async Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await session.SendAndWaitAsync(message, timeout, cancellationToken))?.Data?.Content;

    public async Task SendAsync(MessageOptions message, CancellationToken cancellationToken) =>
        await session.SendAsync(message, cancellationToken);

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
