namespace Fleet.Channels;

/// <summary>Browser Web Push subscription including P-256 public key and auth secret (FR-CLI-06).</summary>
public sealed record WebPushSubscription(string DeviceId, Uri Endpoint, string P256Dh, string AuthSecret);
