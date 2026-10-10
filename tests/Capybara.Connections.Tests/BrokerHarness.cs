using System.Text.Json;
using Capybara.Connections.Broker;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Capybara.Contracts.Connections;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Storage;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Connections.Tests;

/// <summary>
/// A broker over in-memory grants for agent <c>ea</c>, a connector (recording by default) on connection <c>email</c>
/// (Gmail, so Google OAuth), a fake vault and a fake Google token endpoint.
/// </summary>
internal sealed class BrokerHarness : IDisposable
{
    public const string ClientSecretName = "oauth-google-client-secret";

    private readonly TempDirectory _dir = new();

    public BrokerHarness(
        IReadOnlyList<Grant>? grants = null,
        IConnector? connector = null,
        Func<HttpRequestMessage, HttpResponseMessage>? tokenEndpoint = null,
        ISecretStore? secrets = null,
        string? freezeMarker = null)
    {
        Secrets.SetAsync(ClientSecretName, "google-client-secret").AsTask().GetAwaiter().GetResult();
        Grants = new InMemoryGrantProvider().Set("ea", grants ?? [new Grant { Connection = "email", Operations = ["read"] }]);
        Connector = new RecordingConnector(ConnectionDefinition.Create("email", ConnectorKind.Gmail));
        Registry = new ConnectorRegistry([connector ?? Connector]);
        Vault = new TokenVault(secrets ?? Secrets);
        TokenEndpoint = new FakeHttpMessageHandler(tokenEndpoint ?? (_ => throw new InvalidOperationException("no OAuth expected")));
        var registration = new OAuthClientOptions { ClientId = "google-id", ClientSecretName = ClientSecretName, RedirectUri = new Uri("https://capybara.example.ts.net/oauth/callback") };
        Credentials = new CredentialManager(Vault, secrets ?? Secrets, new OAuthClient(new HttpClient(TokenEndpoint)), Router, new Dictionary<CredentialProvider, OAuthClientOptions> { [CredentialProvider.Google] = registration }, Audit, Time);
        FreezeMarker = freezeMarker ?? Path.Combine(_dir.Path, "broker.frozen");
        Freeze = new BrokerFreeze(FreezeMarker, Audit);
        Catalog = new BrokerToolCatalog(Grants, Registry);
        Broker = CreateBroker(Audit);
    }

    public ConnectionBroker CreateBroker(Policy.Audit.IAuditLog audit) =>
        new(new GrantEnforcer(Grants), Catalog, Registry, Credentials, audit, Freeze, NullLogger<ConnectionBroker>.Instance);

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public TestSecretStore Secrets { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public RecordingRouter Router { get; } = new();

    public FakeHttpMessageHandler TokenEndpoint { get; }

    public InMemoryGrantProvider Grants { get; }

    public RecordingConnector Connector { get; }

    public ConnectorRegistry Registry { get; }

    public BrokerToolCatalog Catalog { get; }

    public TokenVault Vault { get; }

    public CredentialManager Credentials { get; }

    public BrokerFreeze Freeze { get; }

    public ConnectionBroker Broker { get; }

    public string FreezeMarker { get; }

    public string ScratchDirectory => _dir.Path;

    public IEnumerable<Policy.Audit.AuditEntry> BrokerCalls => Audit.Entries.Where(e => e.Kind == "broker_call");

    public async Task<string> AuthorizeAsync(string connection = "email")
    {
        var token = "ya29." + Guid.NewGuid().ToString("N");
        await Vault.SetAsync(connection, new ConnectionCredential { AccessToken = token });
        return token;
    }

    /// <summary>An access token about to expire with a refresh token, so the next call refreshes.</summary>
    public Task AuthorizeExpiringAsync(string connection = "email") =>
        Vault.SetAsync(connection, new ConnectionCredential { AccessToken = "ya29.old-token-value", AccessTokenExpiresAt = Time.GetUtcNow(), RefreshToken = "refresh-1", ConsentedAt = Time.GetUtcNow().AddDays(-1) }).AsTask();

    public void Dispose()
    {
        Freeze.Dispose();
        _dir.Dispose();
    }
}

/// <summary>Echoes the bearer it was given so redaction can be observed; mail-shaped operations.</summary>
internal sealed class RecordingConnector(ConnectionDefinition connection) : IConnector
{
    public ConnectionDefinition Connection { get; } = connection;

    public IReadOnlyList<ConnectorOperation> Operations { get; } = MailOperations.All;

    public List<(string Operation, string Args)> Calls { get; } = [];

    public Func<string, JsonElement, BrokerResult>? Behavior { get; set; }

    public ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default)
    {
        Calls.Add((operation, args.GetRawText()));
        if (Behavior is not null)
        {
            return ValueTask.FromResult(Behavior(operation, args));
        }

        var text = $"message body; leaked header Authorization: Bearer {accessToken}";
        return ValueTask.FromResult(new BrokerResult(true, text, Taint.Tainted, text.Length));
    }
}

internal sealed class RecordingRouter : IInteractiveAuthRouter
{
    public List<InteractiveAuthRequest> Requests { get; } = [];

    public bool Fail { get; set; }

    public ValueTask RouteAsync(InteractiveAuthRequest request, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new HttpRequestException("capybarad unreachable");
        }

        Requests.Add(request);
        return ValueTask.CompletedTask;
    }
}
