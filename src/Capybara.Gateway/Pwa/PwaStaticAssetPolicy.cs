namespace Capybara.Gateway.Pwa;

/// <summary>
/// What the tailnet identity may load before a device token is entered (FR-CLI-02): the PWA shell, its static assets
/// and every client-side route, so a notification's deep link (<c>/questions/{id}</c>, <c>/approvals</c>) opens on a
/// browser that holds no token yet. The shell holds no data.
/// </summary>
/// <remarks>
/// Never <c>/api/</c> or <c>/hubs/</c> (case-insensitive, as routing is), and never a request that routing matched to
/// an endpoint other than the shell: whatever the path looks like, an API endpoint is only reached with a device token.
/// </remarks>
public static class PwaStaticAssetPolicy
{
    public static bool AllowsTokenlessGet(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        if (IsApiOrHub(request.Path))
        {
            return false;
        }

        // No endpoint: a static file (or a 404 for one). Otherwise only the shell itself.
        var endpoint = context.GetEndpoint();
        return endpoint is null || endpoint.Metadata.GetMetadata<PwaShellMarker>() is not null;
    }

    public static bool IsApiOrHub(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Endpoint metadata for the PWA shell, the only endpoint served without a device token.</summary>
public sealed class PwaShellMarker
{
    public static PwaShellMarker Instance { get; } = new();

    private PwaShellMarker()
    {
    }
}
