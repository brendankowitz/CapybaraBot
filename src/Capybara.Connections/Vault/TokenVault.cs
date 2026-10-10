using System.Text.Json;
using Capybara.Contracts.Storage;

namespace Capybara.Connections.Vault;

/// <summary>
/// A connection's stored credential (FR-CON-01, FR-CON-05). Only the broker ever holds this. The timestamps are the
/// vault metadata the token health monitor reads.
/// </summary>
public sealed record ConnectionCredential
{
    /// <summary>Access tokens are refreshed this long before they expire.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    public required string AccessToken { get; init; }

    public DateTimeOffset? AccessTokenExpiresAt { get; init; }

    public string? RefreshToken { get; init; }

    /// <summary>When the user last completed consent and a refresh token was issued.</summary>
    public DateTimeOffset? ConsentedAt { get; init; }

    /// <summary>Last successful refresh; Microsoft's 90-day inactivity window counts from here.</summary>
    public DateTimeOffset? RefreshedAt { get; init; }

    /// <summary>Hard expiry of the credential itself, e.g. a fine-grained PAT.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool NeedsRefresh(DateTimeOffset now) =>
        RefreshToken is not null && AccessTokenExpiresAt is { } expires && expires <= now + RefreshSkew;

    /// <summary>Never prints token values, so an accidental log line cannot leak them (FR-SAF-08).</summary>
    public override string ToString() =>
        $"ConnectionCredential {{ AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, HasRefreshToken = {RefreshToken is not null}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// Broker-only credential vault over encrypted secret storage (FR-CON-01, FR-CON-04). A connection's credential lives
/// under the flat secret name <c>connection-{id}</c>, either as JSON or, for admin-seeded PATs, as the bare token.
/// </summary>
public sealed class TokenVault(ISecretStore secrets)
{
    private const string Prefix = "connection-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SecretName(string connectionId) => Prefix + connectionId;

    public async ValueTask<ConnectionCredential?> GetAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var raw = await secrets.GetAsync(SecretName(connectionId), cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.TrimStart().StartsWith('{')
            ? JsonSerializer.Deserialize<ConnectionCredential>(raw, Json)
            : new ConnectionCredential { AccessToken = raw.Trim() };
    }

    public ValueTask SetAsync(string connectionId, ConnectionCredential credential, CancellationToken cancellationToken = default) =>
        secrets.SetAsync(SecretName(connectionId), JsonSerializer.Serialize(credential, Json), cancellationToken);

    public async ValueTask<IReadOnlyList<string>> ListConnectionIdsAsync(CancellationToken cancellationToken = default) =>
        [.. (await secrets.ListAsync(cancellationToken)).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..])];
}
