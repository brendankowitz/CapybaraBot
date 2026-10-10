using System.Text.Json;
using Capybara.Contracts.Storage;

namespace Capybara.Observability.Cost;

/// <summary>
/// Append-only JSONL ledger file (FR-OBS-02, NFR-02). Appends are serialized and flushed; a torn last line from a
/// crash is dropped on load. Place it under the configured state directory.
/// </summary>
public sealed class FileCostLedgerStore : ICostLedgerStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileCostLedgerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public IReadOnlyList<UsageRecord> Load() => JsonlFile.Replay<UsageRecord>(_path, Json);

    public async ValueTask AppendAsync(UsageRecord usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await JsonlFile.AppendAsync(_path, usage, Json, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
