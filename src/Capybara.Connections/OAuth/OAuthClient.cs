using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Connections.OAuth;

/// <summary>
/// RFC 6749 token endpoint client used by the broker for the authorization-code (with RFC 7636 PKCE) and
/// refresh-token grants (FR-CON-04, FR-CON-08). Google and the Microsoft identity platform both speak this protocol.
/// </summary>
public sealed class OAuthClient(HttpClient http)
{
    public ValueTask<OAuthTokenResponse> ExchangeCodeAsync(Uri tokenEndpoint, string clientId, string? clientSecret, string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default) =>
        PostAsync(tokenEndpoint, clientId, clientSecret, [new("grant_type", "authorization_code"), new("code", code), new("code_verifier", codeVerifier), new("redirect_uri", redirectUri.ToString())], cancellationToken);

    public ValueTask<OAuthTokenResponse> RefreshAsync(Uri tokenEndpoint, string clientId, string? clientSecret, string refreshToken, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default) =>
        PostAsync(tokenEndpoint, clientId, clientSecret, [new("grant_type", "refresh_token"), new("refresh_token", refreshToken), new("scope", string.Join(' ', scopes))], cancellationToken);

    private async ValueTask<OAuthTokenResponse> PostAsync(Uri tokenEndpoint, string clientId, string? clientSecret, List<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        form.Add(new("client_id", clientId));
        if (clientSecret is not null)
        {
            form.Add(new("client_secret", clientSecret));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(tokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Error bodies carry only error/error_description, never tokens.
            var error = await ReadErrorAsync(response, cancellationToken);
            throw new OAuthException(error.Error ?? "http_" + (int)response.StatusCode, $"Token endpoint rejected the request: {error.Error} {error.Description}".Trim());
        }

        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
        return token is { AccessToken.Length: > 0 } ? token : throw new OAuthException("invalid_response", "Token endpoint returned no access token.");
    }

    private static async ValueTask<OAuthErrorResponse> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(cancellationToken) ?? new OAuthErrorResponse(null, null);
        }
        catch (JsonException)
        {
            return new OAuthErrorResponse(null, null);
        }
    }

    private sealed record OAuthErrorResponse([property: JsonPropertyName("error")] string? Error, [property: JsonPropertyName("error_description")] string? Description);
}

/// <summary>A token endpoint response. Providers may rotate the refresh token, or omit it to keep the old one.</summary>
public sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null)
{
    public override string ToString() => $"OAuthTokenResponse {{ ExpiresIn = {ExpiresIn}, HasRefreshToken = {RefreshToken is not null} }}";
}
