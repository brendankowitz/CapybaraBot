using Capybara.Connections.Model;

namespace Capybara.Connections.OAuth;

/// <summary>
/// An OAuth client registration for one provider (FR-CON-08). The client secret is a vault secret name, never a
/// value in configuration.
/// </summary>
public sealed class OAuthClientOptions
{
    public string? ClientId { get; set; }

    /// <summary>Vault secret holding the client secret, e.g. <c>oauth-google-client-secret</c>. Omit for public clients.</summary>
    public string? ClientSecretName { get; set; }

    /// <summary>
    /// Where the provider sends the user back. Typically capybarad's tailnet URL, which forwards <c>code</c> and
    /// <c>state</c> to <c>POST /broker/oauth/complete</c>; the PKCE verifier never leaves the broker, so the code is
    /// useless to anyone else.
    /// </summary>
    public Uri? RedirectUri { get; set; }

    public Uri? AuthorizationEndpoint { get; set; }

    public Uri? TokenEndpoint { get; set; }

    /// <summary>Google only. <see langword="false"/> means "Testing" status, whose refresh tokens expire after 7 days (FR-CON-05).</summary>
    public bool InProduction { get; set; } = true;

    public Uri ResolveAuthorizationEndpoint(CredentialProvider provider) => AuthorizationEndpoint ?? provider switch
    {
        CredentialProvider.Google => new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
        CredentialProvider.Microsoft => new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
        _ => throw new InvalidOperationException($"{provider} does not use OAuth."),
    };

    public Uri ResolveTokenEndpoint(CredentialProvider provider) => TokenEndpoint ?? provider switch
    {
        CredentialProvider.Google => new Uri("https://oauth2.googleapis.com/token"),
        CredentialProvider.Microsoft => new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
        _ => throw new InvalidOperationException($"{provider} does not use OAuth."),
    };
}

/// <summary>
/// A consent step routed to the user's device, away from model context (FR-CON-08). It carries no secret: the
/// PKCE verifier stays in the broker.
/// </summary>
public sealed record InteractiveAuthRequest(string Connection, CredentialProvider Provider, Uri AuthorizationUri, DateTimeOffset ExpiresAt);

/// <summary>Delivers an <see cref="InteractiveAuthRequest"/> to the human (FR-CON-08).</summary>
public interface IInteractiveAuthRouter
{
    /// <exception cref="HttpRequestException">The notification could not be delivered.</exception>
    ValueTask RouteAsync(InteractiveAuthRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An OAuth protocol failure. <see cref="Error"/> is the RFC 6749 error code, e.g. <c>invalid_grant</c>.</summary>
public sealed class OAuthException : Exception
{
    public OAuthException()
    {
    }

    public OAuthException(string message)
        : base(message)
    {
    }

    public OAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OAuthException(string error, string message)
        : base(message) => Error = error;

    public string Error { get; } = "unknown";
}
