namespace Capybara.Gateway.Pwa;

/// <summary>Allows the authenticated tailnet identity to load only the PWA shell before device-token entry (FR-CLI-02).</summary>
public static class PwaStaticAssetPolicy
{
    private static readonly string[] ExactPaths = ["/", "/index.html", "/manifest.webmanifest", "/service-worker.js", "/service-worker.published.js", "/favicon.svg"];
    private static readonly string[] Prefixes = ["/_framework/", "/css/", "/js/", "/icons/"];

    public static bool AllowsTokenlessGet(PathString path, string method)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        var value = path.Value ?? string.Empty;
        return ExactPaths.Contains(value, StringComparer.OrdinalIgnoreCase)
            || Prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
