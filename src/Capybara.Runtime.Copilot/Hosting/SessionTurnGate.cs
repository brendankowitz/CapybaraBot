namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// Lets many session turns share one agent's CLI process while an exclusive operation (replacing that process) waits for
/// them to leave (FR-AGT-06). Turns hold a <see cref="Lease"/>; while <see cref="RunExclusiveAsync"/> runs, no new lease
/// is granted, so no turn can start on a process that is about to be replaced. Turns still running when the drain
/// timeout passes are told to stop through <see cref="Lease.AbortToken"/>, and the operation runs once they have left.
/// </summary>
/// <remarks>
/// Thread-safe. Exclusive operations run one at a time. A lease must be disposed exactly once, after its turn has
/// released everything it took from the process (its session).
/// </remarks>
public sealed class SessionTurnGate : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private int _active;
    private bool _closed;
    private TaskCompletionSource _reopened = Completed();
    private TaskCompletionSource? _drained;
    private CancellationTokenSource _abort = new();

    /// <summary>Waits while an exclusive operation runs, then admits one turn.</summary>
    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task reopened;
            lock (_gate)
            {
                if (!_closed)
                {
                    _active++;
                    return new Lease(this, _abort.Token);
                }

                reopened = _reopened.Task;
            }

            await reopened.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Closes the gate to new turns, waits up to <paramref name="drainTimeout"/> for the turns inside to finish, aborts
    /// any still running and waits for them to leave, then runs <paramref name="operation"/> and reopens the gate (also
    /// when it fails). Returns how many turns were aborted.
    /// </summary>
    public async Task<int> RunExclusiveAsync(Func<Task> operation, TimeSpan drainTimeout, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(time);
        await _exclusive.WaitAsync(cancellationToken);
        try
        {
            Task drained;
            CancellationTokenSource abort;
            lock (_gate)
            {
                _closed = true;
                _reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_active == 0)
                {
                    _drained.TrySetResult();
                }

                drained = _drained.Task;
                abort = _abort;
            }

            var aborted = 0;
            try
            {
                try
                {
                    await drained.WaitAsync(drainTimeout, time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    lock (_gate)
                    {
                        aborted = _active;
                    }

                    // Off this thread: cancelling runs the turns' abort callbacks.
                    await abort.CancelAsync();
                    await drained.WaitAsync(cancellationToken);
                }

                await operation();
                return aborted;
            }
            finally
            {
                TaskCompletionSource reopened;
                lock (_gate)
                {
                    if (abort.IsCancellationRequested)
                    {
                        // Every lease holding the cancelled token has left; later turns get a fresh one.
                        _abort = new CancellationTokenSource();
                        abort.Dispose();
                    }

                    _closed = false;
                    _drained = null;
                    reopened = _reopened;
                }

                reopened.TrySetResult();
            }
        }
        finally
        {
            _exclusive.Release();
        }
    }

    public void Dispose()
    {
        _exclusive.Dispose();
        lock (_gate)
        {
            _abort.Dispose();
        }
    }

    private void Leave()
    {
        lock (_gate)
        {
            _active--;
            if (_active == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>One turn's admission. <see cref="AbortToken"/> fires when an exclusive operation gave up waiting for it.</summary>
    public sealed class Lease : IDisposable
    {
        private SessionTurnGate? _owner;

        internal Lease(SessionTurnGate owner, CancellationToken abortToken)
        {
            _owner = owner;
            AbortToken = abortToken;
        }

        public CancellationToken AbortToken { get; }

        /// <summary>The turn was told to stop so the process could be replaced.</summary>
        public bool WasAborted => AbortToken.IsCancellationRequested;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
