using Fleet.Connections.Vault;
using Microsoft.Identity.Client;

namespace Fleet.Connections.OAuth;

/// <summary>OAuth flow abstraction; interactive steps route to a human device (FR-CON-08).</summary>
public interface IOAuthFlow
{
    ValueTask<OAuthTokenResult> GetTokenAsync(string connectionId, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default);
}

public sealed record OAuthTokenResult(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>Interactive auth request routed away from model context (FR-CON-08).</summary>
public sealed record InteractiveAuthRequest(string Provider, Uri AuthorizationUri, string CodeVerifier, IReadOnlyList<string> Scopes);

public interface IInteractiveAuthRouter
{
    ValueTask RouteAsync(InteractiveAuthRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Microsoft OAuth flow using MSAL and broker-persisted token cache (FR-CON-08).</summary>
public sealed class MicrosoftOAuthFlow(TokenVault vault, IPublicClientApplication client, IInteractiveAuthRouter router, TimeProvider time) : IOAuthFlow
{
    public async ValueTask<OAuthTokenResult> GetTokenAsync(string connectionId, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        var cached = await vault.GetTokenAsync(connectionId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return new OAuthTokenResult(cached, time.GetUtcNow().AddMinutes(50));
        }

        var result = await client.AcquireTokenWithDeviceCode(scopes, async code =>
        {
            await router.RouteAsync(new InteractiveAuthRequest("microsoft", new Uri(code.VerificationUrl), code.UserCode, scopes), cancellationToken);
        }).ExecuteAsync(cancellationToken);
        await vault.SetTokenAsync(connectionId, result.AccessToken, cancellationToken);
        return new OAuthTokenResult(result.AccessToken, result.ExpiresOn);
    }
}

/// <summary>Google OAuth flow placeholder that persists broker-held access tokens (FR-CON-08).</summary>
public sealed class GoogleOAuthFlow(TokenVault vault, IInteractiveAuthRouter router, TimeProvider time) : IOAuthFlow
{
    public async ValueTask<OAuthTokenResult> GetTokenAsync(string connectionId, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        var cached = await vault.GetTokenAsync(connectionId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return new OAuthTokenResult(cached, time.GetUtcNow().AddMinutes(50));
        }

        await router.RouteAsync(new InteractiveAuthRequest("google", new Uri("https://accounts.google.com/o/oauth2/v2/auth"), string.Empty, scopes), cancellationToken);
        throw new InvalidOperationException("Google OAuth consent is pending on the user's device.");
    }
}

/// <summary>GitHub token source for fine-grained PAT or GitHub App installation token with one-hour expiry (FR-CON-05).</summary>
public sealed class GitHubTokenSource(TokenVault vault, TimeProvider time)
{
    public async ValueTask<OAuthTokenResult> GetTokenAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var token = await vault.GetTokenAsync(connectionId, cancellationToken) ?? throw new InvalidOperationException("GitHub token is missing.");
        return new OAuthTokenResult(token, time.GetUtcNow().AddHours(1));
    }
}
