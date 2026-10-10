namespace Capybara.Gateway.Pwa;

/// <summary>
/// Serves the PWA shell for <c>/</c>, <c>/index.html</c> and every client-side route (FR-CLI-02), and answers unknown
/// <c>/api/*</c> and <c>/hubs/*</c> paths with a 404 problem, so a missing route is never read as the shell.
/// </summary>
/// <remarks>
/// The shell is the published <c>wwwroot/index.html</c>; when a host has no web root (tests), a minimal built-in shell
/// stands in. Either is sent with <c>Cache-Control: no-cache</c> so a new version is picked up on the next load.
/// </remarks>
public static class PwaShellEndpoints
{
    private const string FallbackShell = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Capybara</title><base href="/"></head><body><div id="app">Loading Capybara…</div><script src="_framework/blazor.webassembly.js"></script></body></html>
        """;

    public static IEndpointRouteBuilder MapPwaShell(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/", ServeShell).WithMetadata(PwaShellMarker.Instance).ExcludeFromDescription();
        endpoints.MapGet("/index.html", ServeShell).WithMetadata(PwaShellMarker.Instance).ExcludeFromDescription();

        // Client-side routes: anything without a file extension that no other endpoint matched.
        endpoints.MapGet("{*path:nonfile}", ServeShell).WithOrder(int.MaxValue).WithMetadata(PwaShellMarker.Instance).ExcludeFromDescription();

        // More specific than the shell's catch-all, so these paths can never fall through to it.
        endpoints.Map("/api/{**path}", NotFound).WithOrder(int.MaxValue).ExcludeFromDescription();
        endpoints.Map("/hubs/{**path}", NotFound).WithOrder(int.MaxValue).ExcludeFromDescription();
        return endpoints;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotFound() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such route.");

    private static IResult ServeShell(HttpContext context, IWebHostEnvironment environment)
    {
        context.Response.Headers.CacheControl = "no-cache";
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");
        return file.Exists
            ? TypedResults.Stream(file.CreateReadStream(), "text/html; charset=utf-8")
            : TypedResults.Content(FallbackShell, "text/html; charset=utf-8");
    }
}
