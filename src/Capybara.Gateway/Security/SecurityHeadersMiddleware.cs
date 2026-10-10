namespace Capybara.Gateway.Security;

/// <summary>
/// Response headers on everything the gateway serves, shell and API alike: the PWA's Content-Security-Policy (as a
/// header it also covers <c>frame-ancestors</c>, which <c>index.html</c>'s meta tag cannot), no referrer and no MIME
/// sniffing. Set when the response starts, so error responses carry them too.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "connect-src 'self' ws: wss:; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; "
        + "form-action 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpResponse)state).Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        }, context.Response);
        return next(context);
    }
}
