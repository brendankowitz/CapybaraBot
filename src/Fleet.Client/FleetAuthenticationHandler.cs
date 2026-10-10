using System.Net.Http.Headers;

namespace Fleet.Client;

/// <summary>Adds the per-device bearer token; local test identity injection is opt-in only (FR-CLI-01).</summary>
public sealed class FleetAuthenticationHandler(FleetClientOptions options) : DelegatingHandler
{
    public const string TailscaleLoginHeader = "Tailscale-User-Login";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(options.DeviceToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.DeviceToken);
        }

        if (!string.IsNullOrWhiteSpace(options.TailscaleLoginForLocalTesting))
        {
            request.Headers.TryAddWithoutValidation(TailscaleLoginHeader, options.TailscaleLoginForLocalTesting);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
