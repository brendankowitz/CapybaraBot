using System.Text;
using System.Text.Json;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.Lifecycle;

namespace Capybara.Scheduling.Triggers;

/// <summary>The outcome of dispatching one matched event routine.</summary>
public sealed record EventRouteResult(string RoutineId, RoutineExecutionResult Result);

/// <summary>
/// Routes an incoming event payload to every event routine whose narrow filter matches and dispatches it
/// (FR-RTN-01, FR-RTN-07). Event payloads are always tainted, whatever the caller says: the router forces
/// <see cref="Taint.Tainted"/> before the payload can reach the runtime.
/// <para>
/// Ingress is the caller's job: the gateway's webhook endpoint verifies the HMAC
/// (<see cref="WebhookEventSource"/>) and calls <see cref="RouteAsync"/>; in-process sources such as
/// <see cref="FileChangeEventSource"/> are connected with <see cref="Attach"/>. A matching routine that is disabled,
/// paused, over budget or blocked still goes through the dispatcher, which records it and raises the alert (FR-RTN-05).
/// </para>
/// </summary>
public sealed class EventRouter(RoutineRegistry registry, RoutineDispatcher dispatcher, IAlertSink alerts, TimeProvider time)
{
    /// <summary>Dispatches <paramref name="payload"/> to matching event routines. An event nobody listens for is not an error.</summary>
    public async Task<IReadOnlyList<EventRouteResult>> RouteAsync(RoutineEventPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Encoding.UTF8.GetByteCount(payload.Json) > WebhookEventSource.MaxPayloadBytes)
        {
            await RejectAsync($"{payload.Source}:{payload.EventType} payload exceeds the {WebhookEventSource.MaxPayloadBytes}-byte cap and was dropped.", cancellationToken);
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload.Json);
        }
        catch (JsonException)
        {
            await RejectAsync($"{payload.Source}:{payload.EventType} payload is not valid JSON and was dropped.", cancellationToken);
            return [];
        }

        using (document)
        {
            var tainted = payload with { Taint = Taint.Tainted };
            var results = new List<EventRouteResult>();
            foreach (var routine in registry.Routines.Where(r => r.Kind == TriggerKind.Event))
            {
                if (!await MatchesAsync(routine, payload, document.RootElement, cancellationToken))
                {
                    continue;
                }

                var firing = new RoutineFiring(routine, time.GetUtcNow(), Guid.NewGuid().ToString("n"), EventPayload: tainted);
                results.Add(new EventRouteResult(routine.Id, await dispatcher.DispatchAsync(firing, cancellationToken)));
            }

            return results;
        }
    }

    /// <summary>Routes everything <paramref name="source"/> fires. Failures raise an alert rather than disappearing in the event handler.</summary>
    public void Attach(IEventTriggerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Fired += OnFired;
    }

    public void Detach(IEventTriggerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Fired -= OnFired;
    }

    private async ValueTask<bool> MatchesAsync(RoutineSpec routine, RoutineEventPayload payload, JsonElement root, CancellationToken cancellationToken)
    {
        try
        {
            return EventFilter.Parse(routine.EventFilter ?? string.Empty).Matches(payload.Source, payload.EventType, root);
        }
        catch (FormatException ex)
        {
            // Validation rejects these at load time; this guards a routine that bypassed it. A broad filter never matches.
            await RejectAsync($"Event routine '{routine.Id}' has an unusable filter and was not triggered: {ex.Message}", cancellationToken);
            return false;
        }
    }

    private ValueTask RejectAsync(string message, CancellationToken cancellationToken) =>
        alerts.PublishAsync(new Alert("routine.event-rejected", AlertSeverity.Warning, message, time.GetUtcNow()), cancellationToken);

    private void OnFired(object? sender, RoutineEventPayload payload) => _ = RouteSafelyAsync(payload);

    private async Task RouteSafelyAsync(RoutineEventPayload payload)
    {
        try
        {
            await RouteAsync(payload);
        }
        catch (Exception ex)
        {
            await RejectAsync($"Routing {payload.Source}:{payload.EventType} failed: {ex.Message}", CancellationToken.None);
        }
    }
}
