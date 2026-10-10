using Capybara.Policy.Audit;

namespace Capybara.Connections.Broker;

/// <summary>
/// The broker half of the kill switch (FR-SAF-06): while frozen every broker call is denied, which revokes all grants
/// at once. The frozen state is persisted to a marker file in the broker's state directory, so a broker restart
/// cannot silently thaw it. Freezing and unfreezing are audited.
/// </summary>
/// <remarks>
/// Thread-safe. Freezing fails safe: the in-memory state flips first and nothing (caller cancellation, a full or
/// read-only disk, a failed audit write) can undo it; such failures are reported in <see cref="FreezeOutcome"/>.
/// Unfreezing fails closed: if the marker cannot be removed the broker stays frozen. capybarad calls
/// <c>POST /broker/freeze</c> and <c>/broker/unfreeze</c>; only it holds the broker API key.
/// </remarks>
public sealed class BrokerFreeze : IDisposable
{
    private readonly string _markerPath;
    private readonly IAuditLog _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _reason;

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

    /// <summary>Freezes immediately. Deliberately ignores cancellation: a kill switch must not be abortable.</summary>
    public async ValueTask<FreezeOutcome> FreezeAsync(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "kill switch engaged" : reason.Trim();
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _reason = reason;
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
                await _audit.AppendAsync(new AuditRecord("broker_freeze", "deny", Detail: error is null ? reason : $"{reason}; {error}"), CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = (error is null ? string.Empty : error + "; ") + $"audit write failed ({ex.GetType().Name})";
            }

            return new FreezeOutcome(error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="IOException">The marker could not be removed; the broker stays frozen.</exception>
    public async ValueTask UnfreezeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            File.Delete(_markerPath);
            _reason = null;
            await _audit.AppendAsync(new AuditRecord("broker_unfreeze", "allow"), CancellationToken.None);
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

/// <summary>The broker is frozen either way; <see cref="Error"/> says what could not be made durable or audited.</summary>
public sealed record FreezeOutcome(string? Error)
{
    public bool Durable => Error is null;
}
