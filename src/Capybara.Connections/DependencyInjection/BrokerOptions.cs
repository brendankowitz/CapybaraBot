using Capybara.Connections.Mcp;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;

namespace Capybara.Connections.DependencyInjection;

/// <summary>capybara-broker configuration, bound from the <c>Broker</c> section (FR-CON-01).</summary>
public sealed class BrokerOptions
{
    public const string Section = "Broker";

    public int Port { get; set; } = 8788;

    public string? UnixSocketPath { get; set; }

    /// <summary>User-owned config root; the broker reads grants from <c>agents/*/agent.yaml</c> under it (read-only).</summary>
    public string ConfigRoot { get; set; } = "/etc/capybara";

    /// <summary>Writable state: encrypted vault, audit log, freeze marker. Defaults to systemd's <c>$STATE_DIRECTORY</c>.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>Decrypted systemd credentials. Defaults to <c>$CREDENTIALS_DIRECTORY</c>.</summary>
    public string? CredentialsDirectory { get; set; }

    /// <summary>Development override for the shared broker API key; production reads the <c>broker-api-key</c> credential.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyCredential { get; set; } = "broker-api-key";

    /// <summary>Credential holding the 32-byte base64 key that encrypts the writable vault (FR-CON-04).</summary>
    public string VaultKeyCredential { get; set; } = "broker-vault-key";

    public List<BrokerConnectionOptions> Connections { get; set; } = [];

    /// <summary>OAuth client registrations keyed by provider name (<c>Google</c>, <c>Microsoft</c>).</summary>
    public Dictionary<string, OAuthClientOptions> OAuth { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vendored MCP servers whose pins are verified at startup (FR-SKL-04).</summary>
    public List<McpServerRegistration> McpServers { get; set; } = [];

    /// <summary>capybarad's loopback base address, for consent notifications (FR-CON-08).</summary>
    public Uri CapybaradUrl { get; set; } = new("http://127.0.0.1:8787");

    public string ResolveStateDirectory() =>
        StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY")
        ?? throw new InvalidOperationException("Broker:StateDirectory is not set and $STATE_DIRECTORY is empty.");

    public string ResolveCredentialsDirectory() =>
        CredentialsDirectory ?? Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")
        ?? throw new InvalidOperationException("Broker:CredentialsDirectory is not set and $CREDENTIALS_DIRECTORY is empty.");

    /// <summary>The shared key capybarad presents, or <see langword="null"/> when none is provisioned (all calls are then refused).</summary>
    public string? ReadApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        var directory = CredentialsDirectory ?? Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        var path = directory is null ? null : Path.Combine(directory, ApiKeyCredential);
        return path is not null && File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}

/// <summary>One configured connection: a user-chosen id and the connector that serves it.</summary>
public sealed class BrokerConnectionOptions
{
    public string Id { get; set; } = string.Empty;

    public ConnectorKind Kind { get; set; }
}
