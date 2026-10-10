using Capybara.Policy.Audit;

namespace Capybara.Connections.Broker;

/// <summary>
/// The broker half of the kill switch (FR-SAF-06): while frozen every broker call is denied, which revokes all grants
/// at once. The frozen state is persisted to a marker file in the broker's state directory, so a broker restart
/// cannot silently thaw it. Freezing and unfreezing are audited.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe. Freezing fails safe: the in-memory state flips first and nothing (caller cancellation, a full or
/// read-only disk, a failed audit write) can undo it; such failures are reported in <see cref="FreezeOutcome"/>.
/// </para>
/// <para>
/// Unfreezing fails closed and is recorded before it takes effect: the <c>broker_unfreeze</c> audit record is written
/// first (if that fails the broker stays frozen), then the marker is removed (if that fails a
/// <c>broker_unfreeze_failed</c> record is written and the broker stays frozen), and only then do calls resume.
/// </para>
/// <para>
/// Connector calls bracket the provider request with <see cref="TryEnterCall"/> and <see cref="ExitCall"/>. Entering
/// counts the call before checking the freeze and freezing sets the state before reading the count, so a call either
/// sees the freeze or is reported in <see cref="FreezeOutcome.InFlightCalls"/>. capybarad calls
/// <c>POST /broker/freeze</c> and <c>/broker/unfreeze</c>; only it holds the broker API key.
/// </para>
/// </remarks>
public sealed class BrokerFreeze : IDisposable
{
    private readonly string _markerPath;
    private readonly IAuditLog _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _reason;
    private int _inFlight;

    public BrokerFreeze(string markerPath, IAuditLog audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        _markerPath = markerPath;
        _audit = audit;
        if (File.Exists(markerPath))
        {
            var reason = File.ReadAllText(markerPath).Trim();
            _reason = reason.Length > 0 ? reason : "frozen before restart";
        }
    }

    public bool IsFrozen => _reason is not null;

    public string? Reason => _reason;

    /// <summary>Provider requests currently in progress; they started before any freeze and cannot be recalled.</summary>
    public int InFlightCalls => Volatile.Read(ref _inFlight);

    /// <summary>Registers a provider request. Returns <see langword="false"/> (and registers nothing) when frozen.</summary>
    public bool TryEnterCall()
    {
        Interlocked.Increment(ref _inFlight);
        if (_reason is null)
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    public void ExitCall() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Freezes immediately. Deliberately ignores cancellation: a kill switch must not be abortable.</summary>
    public async ValueTask<FreezeOutcome> FreezeAsync(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "kill switch engaged" : reason.Trim();
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _reason = reason;
            var inFlight = Interlocked.CompareExchange(ref _inFlight, 0, 0);
            string? error = null;
            try
            {
                await PersistMarkerAsync(reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"frozen in memory only; the freeze marker could not be persisted ({ex.GetType().Name}), so a restart would thaw the broker";
            }

            try
            {
                var detail = $"{reason}; inFlightCalls={inFlight}" + (error is null ? string.Empty : $"; {error}");
                await _audit.AppendAsync(new AuditRecord("broker_freeze", "deny", Detail: detail), CancellationToken.None);
            }
            catch (Exception ex)
            {
                error = (error is null ? string.Empty : error + "; ") + $"audit write failed ({ex.GetType().Name})";
            }

            return new FreezeOutcome(error, inFlight);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Thaws the broker once the thaw is on record and the marker is gone.</summary>
    /// <exception cref="IOException">The audit write or marker removal failed; the broker stays frozen.</exception>
    public async ValueTask UnfreezeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _audit.AppendAsync(new AuditRecord("broker_unfreeze", "allow", Detail: $"previous reason: {_reason ?? "not frozen"}"), CancellationToken.None);
            try
            {
                File.Delete(_markerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    await _audit.AppendAsync(new AuditRecord("broker_unfreeze_failed", "deny", Detail: $"freeze marker could not be removed ({ex.GetType().Name}); broker stays frozen"), CancellationToken.None);
                }
                catch (Exception auditError)
                {
                    throw new AggregateException(ex, auditError);
                }

                throw;
            }

            _reason = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task PersistMarkerAsync(string reason)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_markerPath))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(_markerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(reason), CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

/// <summary>
/// The broker is frozen either way. <see cref="Error"/> says what could not be made durable or audited;
/// <see cref="InFlightCalls"/> counts provider requests that had already started when the freeze landed.
/// </summary>
public sealed record FreezeOutcome(string? Error, int InFlightCalls)
{
    public bool Durable => Error is null;
}
