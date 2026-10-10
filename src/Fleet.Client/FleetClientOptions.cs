namespace Fleet.Client;

/// <summary>Client authentication settings for the fleet gateway (FR-CLI-01).</summary>
public sealed class FleetClientOptions
{
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8787");

    public string? DeviceToken { get; set; }

    public string? TailscaleLoginForLocalTesting { get; set; }
}
