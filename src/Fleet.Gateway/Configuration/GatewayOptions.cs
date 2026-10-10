using Fleet.Policy.Safety;

namespace Fleet.Gateway.Configuration;

/// <summary>Network and identity settings for <c>fleetd</c> (FR-HST-01, FR-HST-04).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Fleet:Gateway";

    /// <summary>Loopback port. Tailscale Serve publishes it to the tailnet; nothing else may reach it.</summary>
    public int Port { get; set; } = 8787;

    /// <summary>The only Tailscale login allowed, as injected by Serve in <c>Tailscale-User-Login</c>.</summary>
    public string AllowedLogin { get; set; } = string.Empty;

    /// <summary>Origins allowed for browser requests and WebSocket upgrades, e.g. <c>https://fleet.tailnet.ts.net</c>.</summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>Per-device bearer tokens. Only SHA-256 hashes are stored.</summary>
    public IList<DeviceTokenOptions> Devices { get; } = [];
}

public sealed class DeviceTokenOptions
{
    public string Name { get; set; } = string.Empty;

    public ControlSurface Surface { get; set; } = ControlSurface.Phone;

    /// <summary>Lower-case hex SHA-256 of the device token.</summary>
    public string Sha256 { get; set; } = string.Empty;
}
