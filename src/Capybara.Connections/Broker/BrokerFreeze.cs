using Capybara.Policy.Audit;

namespace Capybara.Connections.Broker;

/// <summary>
/// The broker half of the kill switch (FR-SAF-06): while frozen every broker call is denied, which revokes all grants
/// at once. The frozen state is persisted to a marker file in the broker's state directory, so a broker restart
/// cannot silently thaw it. Freezing and unfreezing are audited.
/// </summary>
/// <remarks>Thread-safe. capybarad calls <c>POST /broker/freeze</c> and <c>/broker/unfreeze</c>; only it holds the broker API key.</remarks>
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

    public async ValueTask FreezeAsync(string reason, CancellationToken cancellationToken = default)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "kill switch engaged" : reason.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_markerPath))!);
            await File.WriteAllTextAsync(_markerPath, reason, cancellationToken);
            _reason = reason;
            await _audit.AppendAsync(new AuditRecord("broker_freeze", "deny", Detail: reason), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask UnfreezeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            File.Delete(_markerPath);
            _reason = null;
            await _audit.AppendAsync(new AuditRecord("broker_unfreeze", "allow"), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
