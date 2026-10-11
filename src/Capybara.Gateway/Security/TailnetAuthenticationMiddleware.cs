using System.Security.Cryptography;
using System.Text;
using Capybara.Gateway.Configuration;
using Capybara.Gateway.Endpoints;
using Capybara.Gateway.Pwa;
using Capybara.Gateway.Realtime;
using Capybara.Policy.Safety;
using Microsoft.Extensions.Options;

namespace Capybara.Gateway.Security;

/// <summary>
/// Every request needs both the expected Tailscale identity <em>and</em> a per-device bearer token. Loopback origin is
/// never trusted on its own, browser origins are checked, and WebSocket upgrades require an allowed Origin (FR-CLI-01).
/// </summary>
/// <remarks>
/// Two narrow exceptions, both decided here and nowhere else: the PWA shell is served to the tailnet identity without a
/// token (<see cref="PwaStaticAssetPolicy"/>; it holds no data), and the broker posts consent requests to
/// <see cref="BrokerConsentPath"/> with the shared broker key instead of a device token (FR-CON-08). The broker key
/// authenticates that one route only, and a device token never authenticates it. A device configured as
/// <see cref="ControlSurface.EventRelay"/> (the public webhook relay) reaches only the event ingress (FR-RTN-07).
/// </remarks>
public sealed partial class TailnetAuthenticationMiddleware(RequestDelegate next, IOptions<GatewayOptions> options, BrokerCallerKey brokerKey, ILogger<TailnetAuthenticationMiddleware> logger)
{
    public const string TailscaleLoginHeader = "Tailscale-User-Login";
    public const string HealthPath = "/healthz";
    public const string BrokerConsentPath = "/api/connections/auth-requests";

    private readonly GatewayOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.Equals(HealthPath, StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.Equals(BrokerConsentPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsPost(context.Request.Method) || !brokerKey.Matches(context.Request.Headers.Authorization.ToString()))
            {
                LogRejected(logger, context.Request.Path, "broker key");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.SetBrokerCaller();
            await next(context);
            return;
        }

        var failure = Authenticate(context, out var device);
        if (failure is not null)
        {
            LogRejected(logger, context.Request.Path, failure);
            context.Response.StatusCode = failure == "origin" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
            return;
        }

        if (device is not null)
        {
            // An event relay is not a person: it may reach the event ingress and nothing else (hub, shell, any API).
            if (device.Surface == ControlSurface.EventRelay && !EventRelayEndpoint.Allows(context))
            {
                LogRejected(logger, context.Request.Path, "event relay token outside the event ingress");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.SetDevice(device);
        }

        await next(context);
    }

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Null when the request may proceed; <paramref name="device"/> stays null only for the tokenless shell.</summary>
    private string? Authenticate(HttpContext context, out DeviceIdentity? device)
    {
        device = null;
        var login = context.Request.Headers[TailscaleLoginHeader].ToString();
        if (string.IsNullOrEmpty(_options.AllowedLogin) || !string.Equals(login, _options.AllowedLogin, StringComparison.OrdinalIgnoreCase))
        {
            return "identity";
        }

        var origin = context.Request.Headers.Origin.ToString();
        var needsOrigin = context.WebSockets.IsWebSocketRequest;
        if ((needsOrigin || origin.Length > 0) && !_options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return "origin";
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string BearerPrefix = "Bearer ";
        var token = authorization.StartsWith(BearerPrefix, StringComparison.Ordinal) && authorization.Length > BearerPrefix.Length
            ? authorization[BearerPrefix.Length..].Trim()
            : HubAccessToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            return PwaStaticAssetPolicy.AllowsTokenlessGet(context) ? null : "token";
        }

        var match = MatchDevice(token);
        if (match is null)
        {
            return "token";
        }

        device = new DeviceIdentity(login, match.Name, match.Surface);
        return null;
    }

    private static string? HubAccessToken(HttpContext context) =>
        context.Request.Path.StartsWithSegments(CapybaraRealtimeEndpointExtensions.HubPath, StringComparison.OrdinalIgnoreCase)
            ? context.Request.Query["access_token"].ToString()
            : null;

    private DeviceTokenOptions? MatchDevice(string token)
    {
        Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), presented);

        DeviceTokenOptions? match = null;
        foreach (var candidate in _options.Devices)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(candidate.Sha256);
            }
            catch (FormatException)
            {
                continue;
            }

            // Constant-time compare; keep scanning so timing does not reveal which device matched.
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                match ??= candidate;
            }
        }

        return match;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request to {Path}: {Reason}")]
    private static partial void LogRejected(ILogger logger, PathString path, string reason);
}
