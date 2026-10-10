namespace Fleet.Channels.Ntfy;

/// <summary>Self-hosted ntfy configuration for preferred push delivery (FR-CLI-03, NFR-06).</summary>
public sealed record NtfyOptions(Uri BaseUrl, string Topic)
{
    public string? AccessToken { get; init; }

    public bool SupportsSensitiveContent { get; init; }
}
