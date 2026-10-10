namespace Capybara.Gateway.Pwa;

/// <summary>Fallback PWA shell when test hosts do not load static web assets (FR-CLI-02).</summary>
public static class PwaShellEndpoints
{
    private const string Shell = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Capybara</title><base href="/"></head><body><div id="app">Loading Capybara…</div><script src="_framework/blazor.webassembly.js"></script></body></html>
        """;

    public static IEndpointRouteBuilder MapPwaShellFallback(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/", () => Results.Content(Shell, "text/html"));
        endpoints.MapGet("/index.html", () => Results.Content(Shell, "text/html"));
        return endpoints;
    }
}
