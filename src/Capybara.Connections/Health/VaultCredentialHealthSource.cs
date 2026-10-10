using Capybara.Connections.Connectors;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;

namespace Capybara.Connections.Health;

/// <summary>
/// Derives credential health from vault metadata for every configured connection (FR-CON-05): Google "Testing"
/// refresh tokens (7 days from consent), Google's 6-month unused window, Microsoft's 90-day inactivity window
/// (from the last refresh), and PAT expiry. A configured connection with no credential, or whose refresh token was
/// rejected (consent required), is reported as already expired; a PAT without a recorded expiry is reported with an
/// unknown expiry. Neither fails silently at first use.
/// </summary>
public sealed class VaultCredentialHealthSource(
    TokenVault vault,
    ConnectorRegistry connectors,
    IReadOnlyDictionary<CredentialProvider, OAuthClientOptions> registrations,
    TimeProvider time) : ICredentialHealthSource
{
    public static readonly TimeSpan GoogleTestingLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan GoogleUnusedLifetime = TimeSpan.FromDays(180);
    public static readonly TimeSpan MicrosoftInactivityLifetime = TimeSpan.FromDays(90);

    public async ValueTask<IReadOnlyList<CredentialHealth>> ListAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var items = new List<CredentialHealth>();
        foreach (var connection in connectors.List().Select(c => c.Connection))
        {
            var credential = await vault.GetAsync(connection.Id, cancellationToken);
            if (Describe(connection, credential, now) is { } health)
            {
                items.Add(health);
            }
        }

        return items;
    }

    private CredentialHealth? Describe(ConnectionDefinition connection, ConnectionCredential? credential, DateTimeOffset now)
    {
        var inProduction = !registrations.TryGetValue(connection.Provider, out var registration) || registration.InProduction;
        if (credential is null)
        {
            return new CredentialHealth(connection.Id, KindOf(connection.Provider, inProduction), now, inProduction, "no credential stored; authorize the connection from the desktop.");
        }

        var lastUse = credential.RefreshedAt ?? credential.ConsentedAt ?? now;
        return connection.Provider switch
        {
            _ when credential.ConsentRequiredSince is { } since => new CredentialHealth(connection.Id, KindOf(connection.Provider, inProduction), since, inProduction, "consent required: the provider rejected the refresh token; re-authorize the connection from the desktop."),
            CredentialProvider.GitHub when credential.ExpiresAt is { } expires => new CredentialHealth(connection.Id, CredentialKind.GitHubFineGrainedPat, expires),
            CredentialProvider.GitHub => new CredentialHealth(connection.Id, CredentialKind.GitHubFineGrainedPat, null, Detail: """PAT expiry unknown; store it as JSON {"accessToken":"github_pat_...","expiresAt":"<ISO 8601>"} to get expiry alerts."""),
            _ when credential.RefreshToken is null => new CredentialHealth(connection.Id, KindOf(connection.Provider, inProduction), credential.AccessTokenExpiresAt ?? now, inProduction, "no refresh token stored; re-authorize the connection."),
            CredentialProvider.Google when !inProduction => new CredentialHealth(connection.Id, CredentialKind.GoogleRefreshTokenTesting, (credential.ConsentedAt ?? now) + GoogleTestingLifetime, GoogleAppInProduction: false),
            CredentialProvider.Google => new CredentialHealth(connection.Id, CredentialKind.GoogleRefreshToken, lastUse + GoogleUnusedLifetime),
            CredentialProvider.Microsoft => new CredentialHealth(connection.Id, CredentialKind.MicrosoftRefreshToken, lastUse + MicrosoftInactivityLifetime),
            _ => null,
        };
    }

    private static CredentialKind KindOf(CredentialProvider provider, bool inProduction) => provider switch
    {
        CredentialProvider.Google => inProduction ? CredentialKind.GoogleRefreshToken : CredentialKind.GoogleRefreshTokenTesting,
        CredentialProvider.Microsoft => CredentialKind.MicrosoftRefreshToken,
        _ => CredentialKind.GitHubFineGrainedPat,
    };
}
