namespace Capybara.Orchestration.Coordination;

/// <summary>
/// The fleet-wide cap on concurrently running non-main sessions (FR-A2A-11): consults, delegated-task turns, isolated
/// routine runs and incognito turns all take a slot for as long as they run. One instance is shared by the coordinator
/// and every runtime. Never blocks: a caller without a slot is refused and retries later.
/// </summary>
public sealed class SessionLimiter : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public SessionLimiter(A2AOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentNonMainSessions);
        Capacity = options.MaxConcurrentNonMainSessions;
        _slots = new SemaphoreSlim(Capacity, Capacity);
    }

    public int Capacity { get; }

    public int Available => _slots.CurrentCount;

    /// <summary>A slot to dispose when the session stops running, or <see langword="null"/> when none is free.</summary>
    public IDisposable? TryAcquire() => _slots.Wait(0) ? new Lease(_slots) : null;

    public void Dispose() => _slots.Dispose();

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
