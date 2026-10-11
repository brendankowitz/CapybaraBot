using Capybara.Channels;
using Capybara.Client;
using Capybara.Connections.Client;
using Capybara.Connections.Health;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Gateway.Configuration;
using Capybara.Gateway.ConnectionControl;
using Capybara.Gateway.Security;
using Capybara.Policy.Audit;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// Connections as the user sees them (FR-CON-07): broker health, and turning one off or on (desktop only). Adding or
/// removing connections stays out-of-band, in the broker's configuration. Also the broker's consent route (FR-CON-08).
/// </summary>
public static class ConnectionEndpoints
{
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder api)
    {
        var connections = api.MapGroup("/connections").WithTags("Connections");

        connections.MapGet("/", async (BrokerClient broker, ConnectionSwitches switches, PendingConsents consents, BrokerHostOptions options, TimeProvider time, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<ConnectionStatus> statuses;
            IReadOnlyList<CredentialHealth> health;
            try
            {
                statuses = await broker.ListConnectionsAsync(cancellationToken);
                health = await broker.ListCredentialHealthAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The connection broker is not reachable.");
            }

            var now = time.GetUtcNow();
            return Results.Ok(statuses
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new ConnectionDto(
                    s.Id,
                    s.Kind.ToString(),
                    s.Id,
                    switches.IsEnabled(s.Id),
                    Health(s, health.FirstOrDefault(h => h.ConnectionId == s.Id), consents.Find(s.Id), now, options.TokenExpiryLeadTime)))
                .ToList());
        });

        connections.MapPut("/{id}/enabled", async (string id, SetConnectionEnabledDto request, HttpContext context, BrokerClient broker, ConnectionSwitches switches, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (!ConnectionDefinition.IsValidId(id))
            {
                return Results.NotFound();
            }

            try
            {
                if ((await broker.ListConnectionsAsync(cancellationToken)).All(c => c.Id != id))
                {
                    return Results.NotFound();
                }
            }
            catch (HttpRequestException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The connection broker is not reachable.");
            }

            var by = context.GetDevice().PrincipalName;
            if (switches.Set(id, request.Enabled, by))
            {
                await audit.AppendAsync(new AuditRecord("connection", request.Enabled ? "enabled" : "disabled", Detail: $"{id} by {by}"), cancellationToken);
            }

            return Results.NoContent();
        }).RequireDesktop();

        return api;
    }

    /// <summary>
    /// <c>POST /api/connections/auth-requests</c>: the broker asks for the user's consent (FR-CON-08). Authenticated by the
    /// shared broker key in <see cref="TailnetAuthenticationMiddleware"/>, never by a device token. The push carries only
    /// the connection name; the link itself is shown on the authenticated PWA's connections page.
    /// </summary>
    public static IEndpointRouteBuilder MapBrokerConsentEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(TailnetAuthenticationMiddleware.BrokerConsentPath, async (InteractiveAuthRequest request, HttpContext context, PendingConsents consents, NotificationRouter notifications, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (!context.IsBrokerCaller())
            {
                return Results.Unauthorized();
            }

            if (!ConnectionDefinition.IsValidId(request.Connection)
                || request.AuthorizationUri is not { IsAbsoluteUri: true } uri
                || uri.Scheme != Uri.UriSchemeHttps
                || request.ExpiresAt <= time.GetUtcNow())
            {
                return Results.BadRequest("a valid connection, an https authorization link and a future expiry are required");
            }

            consents.Add(request);
            await notifications.RouteAsync(
                new Notification(NotificationKind.NeedsInput, null, $"consent needed: {request.Connection}", string.Empty, NotificationPriority.High, "/connections", IsSensitive: true),
                cancellationToken);
            return Results.Accepted();
        }).WithTags("Connections").ExcludeFromDescription();
        return app;
    }

    private static ConnectionHealthDto Health(ConnectionStatus status, CredentialHealth? health, InteractiveAuthRequest? consent, DateTimeOffset now, TimeSpan leadTime)
    {
        var expiresAt = health?.ExpiresAt ?? status.AccessTokenExpiresAt;
        var state = expiresAt switch
        {
            null => "unknown",
            { } at when at <= now => "expired",
            { } at when at - now <= leadTime => "expiring",
            _ => "ok",
        };
        var detail = consent is not null
            ? $"consent needed until {consent.ExpiresAt:u}: {consent.AuthorizationUri}"
            : !status.Authorized ? "not authorized" : health?.Detail ?? status.Detail;
        return new ConnectionHealthDto(status.Authorized ? state : "unknown", expiresAt, detail);
    }
}
