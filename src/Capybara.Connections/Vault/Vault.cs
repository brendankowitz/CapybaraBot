using System.Text.RegularExpressions;
using Capybara.Contracts.Storage;

namespace Capybara.Connections.Vault;

/// <summary>Broker-only token vault over encrypted secret storage (FR-CON-01, FR-SAF-08).</summary>
public sealed class TokenVault(ISecretStore secrets)
{
    public ValueTask<string?> GetTokenAsync(string connectionId, CancellationToken cancellationToken = default) =>
        secrets.GetAsync(Key(connectionId), cancellationToken);

    public ValueTask SetTokenAsync(string connectionId, string value, CancellationToken cancellationToken = default) =>
        secrets.SetAsync(Key(connectionId), value, cancellationToken);

    public async ValueTask<IReadOnlyList<string>> ListConnectionIdsAsync(CancellationToken cancellationToken = default)
    {
        var names = await secrets.ListAsync(cancellationToken);
        return [.. names.Where(static n => n.StartsWith("connection/", StringComparison.Ordinal)).Select(static n => n[11..])];
    }

    private static string Key(string connectionId) => $"connection/{connectionId}";
}

/// <summary>Read-only secret store backed by systemd <c>$CREDENTIALS_DIRECTORY</c> (FR-HST-02).</summary>
public sealed class CredentialsDirectorySecretStore(string credentialsDirectory) : ISecretStore
{
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException("Credentials directory is read-only.");

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = SafePath(name);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(credentialsDirectory))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>([.. Directory.EnumerateFiles(credentialsDirectory).Select(Path.GetFileName).Where(static n => n is not null).Select(static n => n!)]);
    }

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException("Credentials directory is read-only.");

    private string SafePath(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name))
        {
            throw new ArgumentException("Secret name must be relative.", nameof(name));
        }

        return Path.Combine(credentialsDirectory, name.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>Redacts known secrets and common token formats from tool output (FR-SAF-08).</summary>
public sealed partial class SecretRedactor(IEnumerable<string>? knownSecrets = null)
{
    private readonly string[] _knownSecrets = [.. knownSecrets?.Where(static s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal) ?? []];

    public string Redact(string value)
    {
        var redacted = value;
        foreach (var secret in _knownSecrets)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = GitHubTokenRegex().Replace(redacted, "[REDACTED]");
        redacted = BearerRegex().Replace(redacted, "$1[REDACTED]");
        redacted = GoogleTokenRegex().Replace(redacted, "[REDACTED]");
        redacted = JwtRegex().Replace(redacted, "[REDACTED]");
        redacted = AwsKeyRegex().Replace(redacted, "[REDACTED]");
        return PrivateKeyRegex().Replace(redacted, "-----BEGIN PRIVATE KEY-----\n[REDACTED]\n-----END PRIVATE KEY-----");
    }

    [GeneratedRegex(@"\b(?:ghp_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/=-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\bya29\.[A-Za-z0-9._-]{10,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleTokenRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();
}
