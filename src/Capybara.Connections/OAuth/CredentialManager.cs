using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Capybara.Connections.Model;
using Capybara.Connections.Vault;
using Capybara.Contracts.Storage;

namespace Capybara.Connections.OAuth;

/// <summary>
/// Owns connection credentials inside the broker (FR-CON-01, FR-CON-04, FR-CON-08): hands out access tokens,
/// refreshes OAuth tokens and persists the rotated refresh token to the encrypted vault, and runs the
/// authorization-code + PKCE consent flow whose interactive step is routed to the user's device.
/// </summary>
/// <remarks>
/// Refreshes are serialized per connection: two concurrent refreshes with a rotating refresh token would invalidate
/// each other. Pending consents live in memory for <see cref="ConsentLifetime"/>; a broker restart simply requires the
/// user to start consent again.
/// </remarks>
public sealed class CredentialManager(
    TokenVault vault,
    ISecretStore secrets,
    OAuthClient oauth,
    IInteractiveAuthRouter router,
    IReadOnlyDictionary<CredentialProvider, OAuthClientOptions> registrations,
    TimeProvider time)
{
    public static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingConsent> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns a usable access token, refreshing it first when it is about to expire. When the refresh token has been
    /// revoked or has expired, consent is restarted and routed to the user, and the call is denied.
    /// </summary>
    public async ValueTask<AccessTokenResult> GetAccessTokenAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var credential = await vault.GetAsync(connection.Id, cancellationToken);
        if (Unusable(credential) is { } problem)
        {
            return AccessTokenResult.Fail(problem);
        }

        if (!credential!.NeedsRefresh(time.GetUtcNow()))
        {
            return AccessTokenResult.Ok(credential.AccessToken);
        }

        var gate = _refreshLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited.
            credential = await vault.GetAsync(connection.Id, cancellationToken);
            if (Unusable(credential) is { } again)
            {
                return AccessTokenResult.Fail(again);
            }

            return credential!.NeedsRefresh(time.GetUtcNow())
                ? await RefreshAsync(connection, credential, cancellationToken)
                : AccessTokenResult.Ok(credential.AccessToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Starts consent: creates PKCE and state values held only by the broker and returns the URL the human opens.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection does not use OAuth, or its provider is not registered.</exception>
    public InteractiveAuthRequest BeginAuthorization(ConnectionDefinition connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var registration = Registration(connection.Provider);
        var now = time.GetUtcNow();
        foreach (var expired in _pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray())
        {
            _pending.TryRemove(expired, out _);
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var expiresAt = now + ConsentLifetime;
        _pending[state] = new PendingConsent(connection, verifier, expiresAt);

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", registration.ClientId!),
            new("response_type", "code"),
            new("redirect_uri", registration.RedirectUri!.ToString()),
            new("scope", string.Join(' ', connection.Scopes)),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (connection.Provider == CredentialProvider.Google)
        {
            // Without these Google issues no refresh token.
            query.Add(new("access_type", "offline"));
            query.Add(new("prompt", "consent"));
        }

        var endpoint = registration.ResolveAuthorizationEndpoint(connection.Provider);
        var uri = new Uri($"{endpoint}?{string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}");
        return new InteractiveAuthRequest(connection.Id, connection.Provider, uri, expiresAt);
    }

    /// <summary>Finishes consent with the code the provider returned; the state is single-use.</summary>
    /// <exception cref="OAuthException">Unknown, reused or expired state, or the code exchange failed.</exception>
    public async ValueTask<ConnectionDefinition> CompleteAuthorizationAsync(string state, string code, CancellationToken cancellationToken = default)
    {
        if (!_pending.TryRemove(state, out var pending))
        {
            throw new OAuthException("invalid_state", "Unknown or already used authorization state.");
        }

        var now = time.GetUtcNow();
        if (pending.ExpiresAt <= now)
        {
            throw new OAuthException("invalid_state", "The authorization request expired; start consent again.");
        }

        var connection = pending.Connection;
        var registration = Registration(connection.Provider);
        var secret = await ClientSecretAsync(registration, cancellationToken);
        var token = await oauth.ExchangeCodeAsync(registration.ResolveTokenEndpoint(connection.Provider), registration.ClientId!, secret, code, pending.Verifier, registration.RedirectUri!, cancellationToken);
        await vault.SetAsync(connection.Id, new ConnectionCredential
        {
            AccessToken = token.AccessToken,
            AccessTokenExpiresAt = now.AddSeconds(token.ExpiresIn),
            RefreshToken = token.RefreshToken,
            ConsentedAt = now,
            RefreshedAt = now,
        }, cancellationToken);
        return connection;
    }

    private async ValueTask<AccessTokenResult> RefreshAsync(ConnectionDefinition connection, ConnectionCredential credential, CancellationToken cancellationToken)
    {
        if (!registrations.TryGetValue(connection.Provider, out var registration) || !IsComplete(registration))
        {
            return AccessTokenResult.Fail($"no OAuth client is registered for {connection.Provider}");
        }

        try
        {
            var secret = await ClientSecretAsync(registration, cancellationToken);
            var token = await oauth.RefreshAsync(registration.ResolveTokenEndpoint(connection.Provider), registration.ClientId!, secret, credential.RefreshToken!, connection.Scopes, cancellationToken);
            var now = time.GetUtcNow();
            await vault.SetAsync(connection.Id, credential with
            {
                AccessToken = token.AccessToken,
                AccessTokenExpiresAt = now.AddSeconds(token.ExpiresIn),
                RefreshToken = token.RefreshToken ?? credential.RefreshToken,
                RefreshedAt = now,
            }, cancellationToken);
            return AccessTokenResult.Ok(token.AccessToken);
        }
        catch (OAuthException ex) when (ex.Error == "invalid_grant")
        {
            var request = BeginAuthorization(connection);
            try
            {
                await router.RouteAsync(request, cancellationToken);
                return AccessTokenResult.Fail("authorization expired; consent was sent to the user's device");
            }
            catch (HttpRequestException)
            {
                return AccessTokenResult.Fail("authorization expired and the consent request could not be delivered");
            }
        }
    }

    private string? Unusable(ConnectionCredential? credential) => credential switch
    {
        null => "connection is not authorized",
        { ExpiresAt: { } expires } when expires <= time.GetUtcNow() => "credential expired",
        _ => null,
    };

    private OAuthClientOptions Registration(CredentialProvider provider) =>
        registrations.TryGetValue(provider, out var registration) && IsComplete(registration)
            ? registration
            : throw new InvalidOperationException(provider == CredentialProvider.GitHub
                ? "GitHub connections use a fine-grained PAT provisioned as a systemd credential, not OAuth."
                : $"No OAuth client (clientId, redirectUri) is registered for {provider}.");

    private static bool IsComplete(OAuthClientOptions registration) => !string.IsNullOrWhiteSpace(registration.ClientId) && registration.RedirectUri is not null;

    private async ValueTask<string?> ClientSecretAsync(OAuthClientOptions registration, CancellationToken cancellationToken) =>
        registration.ClientSecretName is null
            ? null
            : await secrets.GetAsync(registration.ClientSecretName, cancellationToken) ?? throw new OAuthException("missing_client_secret", $"Vault secret '{registration.ClientSecretName}' is missing.");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PendingConsent(ConnectionDefinition Connection, string Verifier, DateTimeOffset ExpiresAt);
}

/// <summary>An access token, or why none is available.</summary>
public sealed record AccessTokenResult(string? AccessToken, string? Problem)
{
    public static AccessTokenResult Ok(string token) => new(token, null);

    public static AccessTokenResult Fail(string problem) => new(null, problem);

    public override string ToString() => $"AccessTokenResult {{ HasToken = {AccessToken is not null}, Problem = {Problem} }}";
}
