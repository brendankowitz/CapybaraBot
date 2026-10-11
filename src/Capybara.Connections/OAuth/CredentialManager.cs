using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Connections.Model;
using Capybara.Connections.Vault;
using Capybara.Contracts.Storage;
using Capybara.Policy.Audit;

namespace Capybara.Connections.OAuth;

/// <summary>
/// Owns connection credentials inside the broker (FR-CON-01, FR-CON-04, FR-CON-08): hands out access tokens,
/// refreshes OAuth tokens and persists the rotated refresh token to the encrypted vault, and runs the
/// authorization-code + PKCE consent flow whose interactive step is routed to the user's device.
/// </summary>
/// <remarks>
/// <para>
/// Refreshes are serialized per connection: two concurrent refreshes with a rotating refresh token would invalidate
/// each other. Token-endpoint failures become a denied <see cref="AccessTokenResult"/>; only caller cancellation and
/// vault failures propagate.
/// </para>
/// <para>
/// A revoked or expired refresh token (<c>invalid_grant</c>) is recorded in the vault as
/// <see cref="ConnectionCredential.ConsentRequiredSince"/>. From then on calls are denied without contacting the
/// provider, and at most one consent request per connection is pending (and routed to the user) at a time; a new one
/// is routed only after the previous one expires. Pending consents live in memory for <see cref="ConsentLifetime"/>.
/// Consent requests, completions and consent-required transitions are audited (FR-SAF-11).
/// </para>
/// </remarks>
public sealed class CredentialManager(
    TokenVault vault,
    ISecretStore secrets,
    OAuthClient oauth,
    IInteractiveAuthRouter router,
    IReadOnlyDictionary<CredentialProvider, OAuthClientOptions> registrations,
    IAuditLog audit,
    TimeProvider time)
{
    public static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingConsent> _pendingByConnection = new(StringComparer.Ordinal);
    private readonly Lock _pendingGate = new();

    /// <summary>Returns a usable access token, refreshing it first when it is about to expire.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async ValueTask<AccessTokenResult> GetAccessTokenAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var credential = await vault.GetAsync(connection.Id, cancellationToken);
        if (Unusable(credential) is { } problem)
        {
            return credential?.ConsentRequiredSince is null ? AccessTokenResult.Fail(problem) : await RequireConsentAsync(connection, cancellationToken);
        }

        if (!credential!.NeedsRefresh(time.GetUtcNow()))
        {
            return AccessTokenResult.Ok(credential.AccessToken);
        }

        var gate = _refreshLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed, or hit invalid_grant, while this one waited.
            credential = await vault.GetAsync(connection.Id, cancellationToken);
            if (Unusable(credential) is { } again)
            {
                return credential?.ConsentRequiredSince is null ? AccessTokenResult.Fail(again) : await RequireConsentAsync(connection, cancellationToken);
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
    /// Returns the connection's pending consent request, creating one (PKCE verifier and state held only by the broker)
    /// if none is live. Used by the desktop's "authorize" action; the caller shows the URL to the human.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection does not use OAuth, or its provider is not registered.</exception>
    public async ValueTask<InteractiveAuthRequest> BeginAuthorizationAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var (request, created) = GetOrCreatePending(connection);
        if (created)
        {
            await audit.AppendAsync(new AuditRecord("oauth_consent_requested", "ask", Detail: $"connection={connection.Id}; requested by desktop"), cancellationToken);
        }

        return request;
    }

    /// <summary>Finishes consent with the code the provider returned; the state is single-use. Clears consent-required.</summary>
    /// <exception cref="OAuthException">Unknown, reused or expired state, or the code exchange was rejected.</exception>
    /// <exception cref="HttpRequestException">The token endpoint is unreachable.</exception>
    public async ValueTask<ConnectionDefinition> CompleteAuthorizationAsync(string state, string code, CancellationToken cancellationToken = default)
    {
        PendingConsent? pending;
        lock (_pendingGate)
        {
            pending = _pendingByConnection.Values.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.State), Encoding.ASCII.GetBytes(state ?? string.Empty)));
            if (pending is not null)
            {
                _pendingByConnection.Remove(pending.Connection.Id);
            }
        }

        if (pending is null)
        {
            await audit.AppendAsync(new AuditRecord("oauth_consent_completed", "deny", Detail: "unknown or already used state"), cancellationToken);
            throw new OAuthException("invalid_state", "Unknown or already used authorization state.");
        }

        var connection = pending.Connection;
        var now = time.GetUtcNow();
        if (pending.ExpiresAt <= now)
        {
            await audit.AppendAsync(new AuditRecord("oauth_consent_completed", "deny", Detail: $"connection={connection.Id}; state expired"), cancellationToken);
            throw new OAuthException("invalid_state", "The authorization request expired; start consent again.");
        }

        // Same lock as refresh: a refresh finishing late must not overwrite the freshly consented credential.
        var gate = _refreshLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        OAuthTokenResponse token;
        try
        {
            try
            {
                var registration = Registration(connection.Provider);
                var secret = await ClientSecretAsync(registration, cancellationToken);
                token = await oauth.ExchangeCodeAsync(registration.ResolveTokenEndpoint(connection.Provider), registration.ClientId!, secret, code, pending.Verifier, registration.RedirectUri!, cancellationToken);
            }
            catch (Exception ex) when (ex is OAuthException or HttpRequestException or JsonException)
            {
                await audit.AppendAsync(new AuditRecord("oauth_consent_completed", "deny", Detail: $"connection={connection.Id}; code exchange failed ({Describe(ex)})"), CancellationToken.None);
                throw;
            }

            await vault.SetAsync(connection.Id, new ConnectionCredential
            {
                AccessToken = token.AccessToken,
                AccessTokenExpiresAt = now.AddSeconds(token.ExpiresIn),
                RefreshToken = token.RefreshToken,
                ConsentedAt = now,
                RefreshedAt = now,
            }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
        await audit.AppendAsync(new AuditRecord("oauth_consent_completed", "allow", Detail: $"connection={connection.Id}; refreshToken={(token.RefreshToken is null ? "absent" : "stored")}"), CancellationToken.None);
        return connection;
    }

    private async ValueTask<AccessTokenResult> RefreshAsync(ConnectionDefinition connection, ConnectionCredential credential, CancellationToken cancellationToken)
    {
        if (!registrations.TryGetValue(connection.Provider, out var registration) || !IsComplete(registration))
        {
            return AccessTokenResult.Fail($"no OAuth client is registered for {connection.Provider}");
        }

        OAuthTokenResponse token;
        try
        {
            var secret = await ClientSecretAsync(registration, cancellationToken);
            token = await oauth.RefreshAsync(registration.ResolveTokenEndpoint(connection.Provider), registration.ClientId!, secret, credential.RefreshToken!, connection.Scopes, cancellationToken);
        }
        catch (OAuthException ex) when (ex.Error == "invalid_grant")
        {
            // The refresh token is dead: drop it and remember that only the user can fix this.
            await vault.SetAsync(connection.Id, credential with { RefreshToken = null, ConsentRequiredSince = time.GetUtcNow() }, cancellationToken);
            await audit.AppendAsync(new AuditRecord("oauth_consent_required", "deny", Detail: $"connection={connection.Id}; refresh token rejected (invalid_grant)"), CancellationToken.None);
            return await RequireConsentAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return AccessTokenResult.Fail($"credential refresh failed ({Describe(ex)})");
        }

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

    /// <summary>Denies, routing a consent request to the user only when none is already pending for the connection.</summary>
    private async ValueTask<AccessTokenResult> RequireConsentAsync(ConnectionDefinition connection, CancellationToken cancellationToken)
    {
        InteractiveAuthRequest request;
        bool created;
        try
        {
            (request, created) = GetOrCreatePending(connection);
        }
        catch (InvalidOperationException ex)
        {
            return AccessTokenResult.Fail($"consent required, but consent cannot be started: {ex.Message}");
        }

        if (!created)
        {
            return AccessTokenResult.Fail("consent required; a consent request is already pending on the user's device");
        }

        try
        {
            await router.RouteAsync(request, cancellationToken);
            await audit.AppendAsync(new AuditRecord("oauth_consent_requested", "ask", Detail: $"connection={connection.Id}; routed to the user's device"), CancellationToken.None);
            return AccessTokenResult.Fail("consent required; a consent request was sent to the user's device");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Let the next call retry delivery instead of waiting for an undelivered request to expire. Only this
            // request is withdrawn: one created meanwhile (e.g. from the desktop) stays.
            lock (_pendingGate)
            {
                if (_pendingByConnection.TryGetValue(connection.Id, out var current) && ReferenceEquals(current.Request, request))
                {
                    _pendingByConnection.Remove(connection.Id);
                }
            }

            await audit.AppendAsync(new AuditRecord("oauth_consent_requested", "deny", Detail: $"connection={connection.Id}; delivery failed ({Describe(ex)})"), CancellationToken.None);
            return AccessTokenResult.Fail("consent required, and the consent request could not be delivered");
        }
    }

    private (InteractiveAuthRequest Request, bool Created) GetOrCreatePending(ConnectionDefinition connection)
    {
        var registration = Registration(connection.Provider);
        var now = time.GetUtcNow();
        lock (_pendingGate)
        {
            if (_pendingByConnection.TryGetValue(connection.Id, out var live) && live.ExpiresAt > now)
            {
                return (live.Request, false);
            }

            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var expiresAt = now + ConsentLifetime;
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
            var request = new InteractiveAuthRequest(connection.Id, connection.Provider, uri, expiresAt);
            _pendingByConnection[connection.Id] = new PendingConsent(connection, state, verifier, expiresAt, request);
            return (request, true);
        }
    }

    private string? Unusable(ConnectionCredential? credential) => credential switch
    {
        null => "connection is not authorized",
        { ConsentRequiredSince: not null } => "consent required",
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

    /// <summary>Exception kind for audit and denial text; never the message, which may echo provider data.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        OAuthException oauthError => oauthError.Error,
        OperationCanceledException => "timed out",
        HttpRequestException http when http.StatusCode is { } status => $"HTTP {(int)status}",
        HttpRequestException http => $"unreachable: {http.HttpRequestError}",
        _ => ex.GetType().Name,
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PendingConsent(ConnectionDefinition Connection, string State, string Verifier, DateTimeOffset ExpiresAt, InteractiveAuthRequest Request);
}

/// <summary>An access token, or why none is available.</summary>
public sealed record AccessTokenResult(string? AccessToken, string? Problem)
{
    public static AccessTokenResult Ok(string token) => new(token, null);

    public static AccessTokenResult Fail(string problem) => new(null, problem);

    public override string ToString() => $"AccessTokenResult {{ HasToken = {AccessToken is not null}, Problem = {Problem} }}";
}
