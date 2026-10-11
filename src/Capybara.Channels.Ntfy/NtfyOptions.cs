namespace Capybara.Channels.Ntfy;

/// <summary>Self-hosted ntfy configuration for preferred push delivery (FR-CLI-03, NFR-06).</summary>
public sealed record NtfyOptions(Uri BaseUrl, string Topic)
{
    public string? AccessToken { get; init; }

    /// <summary>
    /// Whether this ntfy server may receive agent and tool names (<see cref="Notification.IsSensitive"/>). Enable for a
    /// self-hosted server reachable only over the tailnet; leave off for public ntfy.sh.
    /// </summary>
    public bool SupportsSensitiveContent { get; init; }

    /// <summary>
    /// Tailnet base URL of the gateway/PWA. ntfy needs absolute URLs, so the click target and action buttons are
    /// only sent when this is set (FR-CLI-03).
    /// </summary>
    public Uri? GatewayBaseUrl { get; init; }
}
