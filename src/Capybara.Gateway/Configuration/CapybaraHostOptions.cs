namespace Capybara.Gateway.Configuration;

/// <summary>Where user-owned config and mutable state live (FR-HST-02: <c>/etc/capybara</c> read-only, <c>/var/lib/capybara</c> writable).</summary>
public sealed class CapybaraHostOptions
{
    public const string SectionName = "Capybara";

    public string ConfigRoot { get; set; } = "/etc/capybara";

    public string StateRoot { get; set; } = "/var/lib/capybara";

    /// <summary>Name of the systemd credential holding the Copilot PAT (read from <c>$CREDENTIALS_DIRECTORY</c>).</summary>
    public string GitHubTokenCredential { get; set; } = "github-token";
}
