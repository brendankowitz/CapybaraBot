using System.Security.Cryptography;
using System.Text;
using Fleet.Gateway.Configuration;
using Fleet.Gateway.Pwa;
using Fleet.Gateway.Realtime;
using Fleet.Policy.Safety;
using Microsoft.Extensions.Options;

namespace Fleet.Gateway.Security;

/// <summary>
/// Every request needs both the expected Tailscale identity <em>and</em> a per-device bearer token. Loopback origin is
/// never trusted on its own, browser origins are checked, and WebSocket upgrades require an allowed Origin (FR-HST-04).
/// </summary>
public sealed partial class TailnetAuthenticationMiddleware(RequestDelegate next, IOptions<GatewayOptions> options, ILogger<TailnetAuthenticationMiddleware> logger)
{
    public const string TailscaleLoginHeader = "Tailscale-User-Login";
    public const string HealthPath = "/healthz";

    private readonly GatewayOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.Equals(HealthPath, StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method))
        {
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

        context.SetDevice(device!);
        await next(context);
    }

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

        if (PwaStaticAssetPolicy.AllowsTokenlessGet(context.Request.Path, context.Request.Method))
        {
            device = new DeviceIdentity(login, "pwa-bootstrap", ControlSurface.Phone);
            return null;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string BearerPrefix = "Bearer ";
        var token = authorization.StartsWith(BearerPrefix, StringComparison.Ordinal) && authorization.Length > BearerPrefix.Length
            ? authorization[BearerPrefix.Length..].Trim()
            : HubAccessToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            return "token";
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
        context.Request.Path.StartsWithSegments(FleetRealtimeEndpointExtensions.HubPath, StringComparison.Ordinal)
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

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request to {Path}: {Reason}")]
    private static partial void LogRejected(ILogger logger, PathString path, string reason);
}
