using System.Threading.Channels;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.Triggers;

namespace Capybara.Gateway.Events;

/// <summary><c>Capybara:Events</c>: webhook sources accepted by <c>POST /api/events/{source}/{type}</c> (FR-RTN-07).</summary>
public sealed class EventIngressOptions
{
    public const string SectionName = "Capybara:Events";

    /// <summary>Accepted sources by name (e.g. <c>github</c>); a source that is not listed, or has no secret, is refused.</summary>
    public Dictionary<string, EventSourceOptions> Sources { get; } = new(StringComparer.Ordinal);
}

public sealed class EventSourceOptions
{
    /// <summary>systemd credential holding the HMAC-SHA256 shared secret.</summary>
    public string? SecretCredential { get; set; }

    /// <summary>Development and test override for the secret; production reads <see cref="SecretCredential"/>.</summary>
    public string? Secret { get; set; }
}

/// <summary>The HMAC secret of every accepted event source, resolved once at start.</summary>
public sealed class EventIngressSecrets(IReadOnlyDictionary<string, byte[]> secrets)
{
    public byte[]? Find(string source) => secrets.GetValueOrDefault(source);
}

/// <summary>
/// Hands verified webhook events to the <see cref="EventRouter"/> off the request path (FR-RTN-07): the sender gets
/// 202 as soon as the event is queued, and a long routine run never holds a webhook connection open.
/// </summary>
/// <remarks>
/// Events are routed one at a time, in arrival order. The queue is bounded (<see cref="Capacity"/>); when it is full the
/// endpoint answers 503 so the sender retries, rather than the gateway buffering without limit. Events still queued
/// at shutdown are not routed (the senders' retries cover them); a routing failure raises an alert.
/// </remarks>
public sealed partial class EventIngressWorker(EventRouter router, IAlertSink alerts, TimeProvider time, ILogger<EventIngressWorker> logger) : BackgroundService
{
    public const int Capacity = 64;
    public const string FailedAlertKind = "routine.event-failed";

    private readonly Channel<RoutineEventPayload> _queue = Channel.CreateBounded<RoutineEventPayload>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Queues a verified, tainted event; <see langword="false"/> when the queue is full.</summary>
    public bool TryEnqueue(RoutineEventPayload payload) => _queue.Writer.TryWrite(payload);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var payload in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await router.RouteAsync(payload, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One event that fails to route must not stop the ones behind it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogRouteFailed(logger, ex, payload.Source, payload.EventType);
                    await AlertAsync(payload, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task AlertAsync(RoutineEventPayload payload, Exception failure)
    {
        try
        {
            await alerts.PublishAsync(new Alert(FailedAlertKind, AlertSeverity.Warning, $"Routing the {payload.Source}:{payload.EventType} event failed ({failure.GetType().Name}).", time.GetUtcNow()), CancellationToken.None);
        }
#pragma warning disable CA1031 // Already logged; the queue keeps going.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRouteFailed(logger, ex, payload.Source, payload.EventType);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Routing the {Source}:{EventType} event failed")]
    private static partial void LogRouteFailed(ILogger logger, Exception exception, string source, string eventType);
}
