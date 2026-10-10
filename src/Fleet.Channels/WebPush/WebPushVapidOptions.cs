namespace Fleet.Channels.WebPush;

/// <summary>VAPID identity used to authenticate Web Push requests (FR-CLI-06).</summary>
public sealed record WebPushVapidOptions(string Subject, string PublicKey, string PrivateKey);
