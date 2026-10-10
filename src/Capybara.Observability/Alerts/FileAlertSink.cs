using System.Text.Json;
using System.Text.Json.Serialization;
using Capybara.Contracts.Storage;

namespace Capybara.Observability.Alerts;

/// <summary>
/// Durable alert sink (FR-OBS-05, NFR-02): appends each alert to a JSONL file and replays the most recent
/// configured capacity on startup so the gateway's recent-alerts view survives a restart. A torn last line
/// from a crash is dropped.
/// </summary>
public sealed class FileAlertSink : IAlertSink, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly int _capacity;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _bufferGate = new();
    private readonly Queue<Alert> _recent = new();

    public FileAlertSink(string path, int capacity = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _path = path;
        _capacity = capacity;
        foreach (var alert in JsonlFile.Replay<Alert>(path, Json))
        {
            Buffer(alert);
        }
    }

    /// <summary>The most recent alerts, oldest first, including those replayed from disk.</summary>
    public IReadOnlyList<Alert> Recent
    {
        get
        {
            lock (_bufferGate)
            {
                return [.. _recent];
            }
        }
    }

    public async ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await JsonlFile.AppendAsync(_path, alert, Json, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        Buffer(alert);
    }

    public void Dispose() => _writeLock.Dispose();

    private void Buffer(Alert alert)
    {
        lock (_bufferGate)
        {
            _recent.Enqueue(alert);
            while (_recent.Count > _capacity)
            {
                _recent.Dequeue();
            }
        }
    }
}
