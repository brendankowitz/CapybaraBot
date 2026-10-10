using Capybara.Gateway.Events;
using Capybara.Scheduling.Triggers;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// <c>POST /api/events/{source}/{type}</c>: webhook events for event routines (FR-RTN-01, FR-RTN-07). Reachable only
/// with a device token over the tailnet (like every <c>/api</c> route, so a public sender needs a relay on the tailnet),
/// and only with a valid HMAC-SHA256 of the body (<c>X-Hub-Signature-256: sha256=…</c>) under the source's own secret.
/// The payload is capped and always tainted, and routing happens off the request (<see cref="EventIngressWorker"/>).
/// </summary>
public static class EventIngressEndpoints
{
    public const string SignatureHeader = "X-Hub-Signature-256";

    public static IEndpointRouteBuilder MapEventIngressEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/events/{source}/{type}", async (string source, string type, HttpContext context, EventIngressSecrets secrets, EventIngressWorker queue, CancellationToken cancellationToken) =>
        {
            if (!IsName(source) || !IsName(type) || secrets.Find(source) is not { } secret)
            {
                return Results.NotFound();
            }

            if (context.Request.ContentLength > WebhookEventSource.MaxPayloadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Read at most one byte past the cap, whatever Content-Length claimed.
            var body = new byte[WebhookEventSource.MaxPayloadBytes + 1];
            var length = 0;
            int read;
            while (length < body.Length && (read = await context.Request.Body.ReadAsync(body.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
            }

            if (length > WebhookEventSource.MaxPayloadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var payload = body.AsSpan(0, length);
            if (!WebhookEventSource.VerifySharedSecretSignature(payload, context.Request.Headers[SignatureHeader].ToString(), secret))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The event signature does not match.");
            }

            return queue.TryEnqueue(WebhookEventSource.CreatePayload(source, type, payload))
                ? Results.Accepted()
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Too many events are waiting; retry later.");
        }).WithTags("Events");

        return api;
    }

    private static bool IsName(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');
}
