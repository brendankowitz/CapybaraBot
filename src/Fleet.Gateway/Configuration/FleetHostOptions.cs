namespace Fleet.Gateway.Configuration;

/// <summary>Where user-owned config and mutable state live (FR-HST-02: <c>/etc/fleet</c> read-only, <c>/var/lib/fleet</c> writable).</summary>
public sealed class FleetHostOptions
{
    public const string SectionName = "Fleet";

    public string ConfigRoot { get; set; } = "/etc/fleet";

    public string StateRoot { get; set; } = "/var/lib/fleet";

    /// <summary>Name of the systemd credential holding the Copilot PAT (read from <c>$CREDENTIALS_DIRECTORY</c>).</summary>
    public string GitHubTokenCredential { get; set; } = "github-token";
}
