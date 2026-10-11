using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Connections.Tests;

public sealed class CredentialManagerTests
{
    private static readonly ConnectionDefinition Mail = ConnectionDefinition.Create("email", ConnectorKind.GraphMail);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly TestSecretStore _secrets = new();
    private readonly RecordingRouter _router = new();
    private readonly InMemoryAuditLog _audit = new();
    private readonly List<NameValueCollection> _forms = [];
    private Func<NameValueCollection, HttpResponseMessage> _tokenEndpoint = _ => Token("new-access", "rotated-refresh");

    [Fact]
    public async Task Fresh_access_token_is_returned_without_a_refresh()
    {
        var manager = Create();
        await new TokenVault(_secrets).SetAsync("email", new ConnectionCredential { AccessToken = "current", AccessTokenExpiresAt = _time.GetUtcNow().AddMinutes(30), RefreshToken = "refresh" }, TestContext.Current.CancellationToken);

        var result = await manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken);

        Assert.Equal("current", result.AccessToken);
        Assert.Empty(_forms);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_and_the_rotated_refresh_token_is_persisted()
    {
        var manager = Create();
        var vault = new TokenVault(_secrets);
        await vault.SetAsync("email", new ConnectionCredential { AccessToken = "old", AccessTokenExpiresAt = _time.GetUtcNow().AddSeconds(30), RefreshToken = "refresh-1", ConsentedAt = _time.GetUtcNow().AddDays(-10) }, TestContext.Current.CancellationToken);

        var result = await manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken);

        Assert.Equal("new-access", result.AccessToken);
        var form = Assert.Single(_forms);
        Assert.Equal(("refresh_token", "refresh-1", "client-id", "client-secret-value"), (form["grant_type"], form["refresh_token"], form["client_id"], form["client_secret"]));
        Assert.Contains("offline_access", form["scope"], StringComparison.Ordinal);
        var stored = await vault.GetAsync("email", TestContext.Current.CancellationToken);
        Assert.Equal(("new-access", "rotated-refresh"), (stored!.AccessToken, stored.RefreshToken));
        Assert.Equal(_time.GetUtcNow(), stored.RefreshedAt);
        Assert.Equal(_time.GetUtcNow().AddDays(-10), stored.ConsentedAt);
        Assert.Equal(_time.GetUtcNow().AddHours(1), stored.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var gate = new TaskCompletionSource();
        var manager = Create(new GatedHandler(gate.Task, this));
        await new TokenVault(_secrets).SetAsync("email", new ConnectionCredential { AccessToken = "old", AccessTokenExpiresAt = _time.GetUtcNow(), RefreshToken = "refresh-1" }, TestContext.Current.CancellationToken);

        var calls = Enumerable.Range(0, 5).Select(_ => manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken).AsTask()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.Equal("new-access", r.AccessToken));
        Assert.Single(_forms);
    }

    [Fact]
    public async Task Revoked_refresh_token_routes_one_consent_and_stops_contacting_the_provider()
    {
        _tokenEndpoint = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant","error_description":"expired"}""") };
        var manager = Create();
        var vault = new TokenVault(_secrets);
        var ct = TestContext.Current.CancellationToken;
        await vault.SetAsync("email", new ConnectionCredential { AccessToken = "old", AccessTokenExpiresAt = _time.GetUtcNow(), RefreshToken = "dead" }, ct);

        var first = await manager.GetAccessTokenAsync(Mail, ct);
        var repeats = new List<AccessTokenResult>();
        for (var i = 0; i < 5; i++)
        {
            repeats.Add(await manager.GetAccessTokenAsync(Mail, ct));
        }

        Assert.Contains("consent request was sent", first.Problem, StringComparison.Ordinal);
        Assert.All(repeats, r => Assert.Contains("already pending", r.Problem, StringComparison.Ordinal));
        Assert.Single(_forms);
        var request = Assert.Single(_router.Requests);
        Assert.Equal("email", request.Connection);
        Assert.DoesNotContain("verifier", request.AuthorizationUri.Query, StringComparison.Ordinal);
        var stored = await vault.GetAsync("email", ct);
        Assert.Equal((null, _time.GetUtcNow()), (stored!.RefreshToken, stored.ConsentRequiredSince));
        Assert.Contains(_audit.Entries, e => e.Kind == "oauth_consent_required");

        // After the pending request expires, exactly one new request is routed; the provider is still not contacted.
        _time.Advance(CredentialManager.ConsentLifetime + TimeSpan.FromSeconds(1));
        await manager.GetAccessTokenAsync(Mail, ct);
        await manager.GetAccessTokenAsync(Mail, ct);
        Assert.Equal(2, _router.Requests.Count);
        Assert.Single(_forms);

        // Completing consent clears the marker.
        _tokenEndpoint = _ => Token("fresh", "new-refresh");
        await manager.CompleteAuthorizationAsync(HttpUtility.ParseQueryString(_router.Requests[^1].AuthorizationUri.Query)["state"]!, "code", ct);
        Assert.Equal("fresh", (await manager.GetAccessTokenAsync(Mail, ct)).AccessToken);
        Assert.Null((await vault.GetAsync("email", ct))!.ConsentRequiredSince);
    }

    [Fact]
    public async Task Concurrent_callers_with_consent_required_route_a_single_request()
    {
        var manager = Create();
        await new TokenVault(_secrets).SetAsync("email", new ConnectionCredential { AccessToken = "old", ConsentRequiredSince = _time.GetUtcNow() }, TestContext.Current.CancellationToken);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken).AsTask()));

        Assert.Single(_router.Requests);
        Assert.Empty(_forms);
    }

    [Fact]
    public async Task Undelivered_consent_request_is_retried_on_the_next_call()
    {
        var manager = Create();
        await new TokenVault(_secrets).SetAsync("email", new ConnectionCredential { AccessToken = "old", ConsentRequiredSince = _time.GetUtcNow() }, TestContext.Current.CancellationToken);
        _router.Fail = true;

        var failed = await manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken);
        _router.Fail = false;
        await manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken);

        Assert.Contains("could not be delivered", failed.Problem, StringComparison.Ordinal);
        Assert.Single(_router.Requests);
    }
    [Fact]
    public async Task Consent_uses_pkce_and_stores_the_refresh_token_once()
    {
        var manager = Create();
        var ct = TestContext.Current.CancellationToken;

        var request = await manager.BeginAuthorizationAsync(Mail, ct);
        var query = HttpUtility.ParseQueryString(request.AuthorizationUri.Query);
        await manager.CompleteAuthorizationAsync(query["state"]!, "auth-code", ct);

        Assert.Equal("S256", query["code_challenge_method"]);
        var form = Assert.Single(_forms);
        Assert.Equal(("authorization_code", "auth-code"), (form["grant_type"], form["code"]));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        var stored = await new TokenVault(_secrets).GetAsync("email", ct);
        Assert.Equal(("new-access", "rotated-refresh", _time.GetUtcNow()), (stored!.AccessToken, stored.RefreshToken, stored.ConsentedAt));
        await Assert.ThrowsAsync<OAuthException>(async () => await manager.CompleteAuthorizationAsync(query["state"]!, "auth-code", ct));
        Assert.Equal(["oauth_consent_requested", "oauth_consent_completed", "oauth_consent_completed"], _audit.Entries.Select(e => e.Kind));
        Assert.Equal(["ask", "allow", "deny"], _audit.Entries.Select(e => e.Decision));
    }

    [Fact]
    public async Task Consent_state_expires()
    {
        var manager = Create();
        var state = HttpUtility.ParseQueryString((await manager.BeginAuthorizationAsync(Mail, TestContext.Current.CancellationToken)).AuthorizationUri.Query)["state"]!;

        _time.Advance(CredentialManager.ConsentLifetime + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<OAuthException>(async () => await manager.CompleteAuthorizationAsync(state, "code", TestContext.Current.CancellationToken));
        Assert.Empty(_forms);
    }

    [Fact]
    public async Task Google_consent_requests_offline_access()
    {
        var manager = Create();

        var request = await manager.BeginAuthorizationAsync(ConnectionDefinition.Create("calendar", ConnectorKind.GoogleCalendar), TestContext.Current.CancellationToken);

        var query = HttpUtility.ParseQueryString(request.AuthorizationUri.Query);
        Assert.Equal(("offline", "consent"), (query["access_type"], query["prompt"]));
        Assert.StartsWith("https://accounts.google.com/", request.AuthorizationUri.AbsoluteUri, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Missing_or_expired_credentials_are_denied()
    {
        var manager = Create();
        var github = ConnectionDefinition.Create("github", ConnectorKind.GitHub);
        await new TokenVault(_secrets).SetAsync("github", new ConnectionCredential { AccessToken = "github_pat_x", ExpiresAt = _time.GetUtcNow().AddMinutes(-1) }, TestContext.Current.CancellationToken);

        Assert.Equal("connection is not authorized", (await manager.GetAccessTokenAsync(Mail, TestContext.Current.CancellationToken)).Problem);
        Assert.Equal("credential expired", (await manager.GetAccessTokenAsync(github, TestContext.Current.CancellationToken)).Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.BeginAuthorizationAsync(github, TestContext.Current.CancellationToken));
    }

    private CredentialManager Create(HttpMessageHandler? handler = null)
    {
        _secrets.SetAsync("oauth-microsoft-client-secret", "client-secret-value").AsTask().GetAwaiter().GetResult();
        var registration = new OAuthClientOptions { ClientId = "client-id", ClientSecretName = "oauth-microsoft-client-secret", RedirectUri = new Uri("https://capybara.example.ts.net/oauth/callback") };
        var google = new OAuthClientOptions { ClientId = "google-id", RedirectUri = new Uri("https://capybara.example.ts.net/oauth/callback") };
        var http = new HttpClient(handler ?? new FakeHttpMessageHandler(Respond));
        return new CredentialManager(new TokenVault(_secrets), _secrets, new OAuthClient(http), _router, new Dictionary<CredentialProvider, OAuthClientOptions> { [CredentialProvider.Microsoft] = registration, [CredentialProvider.Google] = google }, _audit, _time);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var form = HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        lock (_forms)
        {
            _forms.Add(form);
        }

        return _tokenEndpoint(form);
    }

    private static HttpResponseMessage Token(string access, string? refresh) =>
        FakeHttpMessageHandler.Json($$"""{"access_token":"{{access}}","expires_in":3600{{(refresh is null ? string.Empty : $",\"refresh_token\":\"{refresh}\"")}}}""");

    private sealed class GatedHandler(Task gate, CredentialManagerTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate;
            return owner.Respond(request);
        }
    }
}
