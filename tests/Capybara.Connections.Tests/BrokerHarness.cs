using System.Text.Json;
using Capybara.Connections.Broker;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Capybara.Contracts.Connections;
using Capybara.Contracts.Messaging;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Connections.Tests;

/// <summary>A broker over in-memory grants, a recording connector and a fake vault.</summary>
internal sealed class BrokerHarness : IDisposable
{
    private readonly TempDirectory _dir = new();

    public BrokerHarness(params Grant[] eaGrants)
    {
        Grants = new InMemoryGrantProvider().Set("ea", eaGrants.Length > 0 ? eaGrants : [new Grant { Connection = "email", Operations = ["read"] }]);
        Connector = new RecordingConnector(ConnectionDefinition.Create("email", ConnectorKind.Gmail));
        Registry = new ConnectorRegistry([Connector]);
        Vault = new TokenVault(Secrets);
        Credentials = new CredentialManager(Vault, Secrets, new OAuthClient(new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no OAuth expected")))), Router, new Dictionary<CredentialProvider, OAuthClientOptions>(), Time);
        Freeze = new BrokerFreeze(Path.Combine(_dir.Path, "broker.frozen"), Audit);
        Broker = new ConnectionBroker(new GrantEnforcer(Grants), Registry, Credentials, Audit, Freeze);
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-10T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public TestSecretStore Secrets { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public RecordingRouter Router { get; } = new();

    public InMemoryGrantProvider Grants { get; }

    public RecordingConnector Connector { get; }

    public ConnectorRegistry Registry { get; }

    public TokenVault Vault { get; }

    public CredentialManager Credentials { get; }

    public BrokerFreeze Freeze { get; }

    public ConnectionBroker Broker { get; }

    public string FreezeMarker => Path.Combine(_dir.Path, "broker.frozen");

    public async Task<string> AuthorizeAsync(string connection = "email")
    {
        var token = "ya29." + Guid.NewGuid().ToString("N");
        await Vault.SetAsync(connection, new ConnectionCredential { AccessToken = token });
        return token;
    }

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

    public ValueTask RouteAsync(InteractiveAuthRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.CompletedTask;
    }
}
