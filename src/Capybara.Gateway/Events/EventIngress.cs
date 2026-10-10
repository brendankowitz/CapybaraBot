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
/// <para>
/// Events are routed one at a time, in arrival order. The queue is bounded (<see cref="Capacity"/>); when it is full the
/// endpoint answers 503 so the sender retries, rather than the gateway buffering without limit. A routing failure raises
/// an alert.
/// </para>
/// <para>
/// A 202 tells the sender the event was taken, so it will not resend it. On shutdown the queue is closed and drained for
/// up to <see cref="DrainTimeout"/> (wall-clock time); events still queued after that, and one cut off mid-route, are lost,
/// and their number is logged and raised as an alert so they can be replayed from the source.
/// </para>
/// </remarks>
public sealed partial class EventIngressWorker(EventRouter router, IAlertSink alerts, TimeProvider time, ILogger<EventIngressWorker> logger) : BackgroundService
{
    public const int Capacity = 64;
    public const string FailedAlertKind = "routine.event-failed";
    public const string DroppedAlertKind = "routine.events-dropped";

    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<RoutineEventPayload> _queue = Channel.CreateBounded<RoutineEventPayload>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    // Cancelled DrainTimeout after shutdown begins; until then queued events are still routed. Not the host's stopping
    // token, which is cancelled the moment shutdown begins.
    private readonly CancellationTokenSource _drain = new();

    /// <summary>Queues a verified, tainted event; <see langword="false"/> when the queue is full or shutting down.</summary>
    public bool TryEnqueue(RoutineEventPayload payload) => _queue.Writer.TryWrite(payload);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        _drain.CancelAfter(DrainTimeout);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _drain.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drain = _drain.Token;
        var cutOff = 0;
        try
        {
            await foreach (var payload in _queue.Reader.ReadAllAsync(drain))
            {
                try
                {
                    await router.RouteAsync(payload, drain);
                }
                catch (OperationCanceledException) when (drain.IsCancellationRequested)
                {
                    cutOff = 1;
                    break;
                }
#pragma warning disable CA1031 // One event that fails to route must not stop the ones behind it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogRouteFailed(logger, ex, payload.Source, payload.EventType);
                    await AlertAsync(new Alert(FailedAlertKind, AlertSeverity.Warning, $"Routing the {payload.Source}:{payload.EventType} event failed ({ex.GetType().Name}).", time.GetUtcNow()));
                }
            }
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested)
        {
        }

        var dropped = cutOff;
        while (_queue.Reader.TryRead(out _))
        {
            dropped++;
        }

        if (dropped > 0)
        {
            LogDropped(logger, dropped);
            await AlertAsync(new Alert(DroppedAlertKind, AlertSeverity.Warning, $"{dropped} accepted webhook event(s) were not routed before capybarad stopped; replay them from the source.", time.GetUtcNow()));
        }
    }

    private async Task AlertAsync(Alert alert)
    {
        try
        {
            await alerts.PublishAsync(alert, CancellationToken.None);
        }
#pragma warning disable CA1031 // Already logged; the queue keeps going.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(logger, ex, alert.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Routing the {Source}:{EventType} event failed")]
    private static partial void LogRouteFailed(ILogger logger, Exception exception, string source, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} accepted webhook events were not routed before shutdown")]
    private static partial void LogDropped(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish alert {Kind}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string kind);
}