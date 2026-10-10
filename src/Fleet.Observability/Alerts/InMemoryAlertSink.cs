using System.Collections.Concurrent;

namespace Fleet.Observability.Alerts;

/// <summary>Bounded in-memory alert buffer surfaced by the gateway.</summary>
public sealed class InMemoryAlertSink(int capacity = 500) : IAlertSink
{
    private readonly ConcurrentQueue<Alert> _alerts = new();

    public IReadOnlyList<Alert> Recent => [.. _alerts];

    public ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        _alerts.Enqueue(alert);
        while (_alerts.Count > capacity && _alerts.TryDequeue(out _))
        {
        }

        return ValueTask.CompletedTask;
    }
}
