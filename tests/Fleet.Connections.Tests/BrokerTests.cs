using System.Net;
using System.Text.Json;
using Fleet.Connections.Broker;
using Fleet.Connections.Connectors;
using Fleet.Connections.Grants;
using Fleet.Connections.Mcp;
using Fleet.Connections.Vault;
using Fleet.Contracts.Connections;
using Fleet.Contracts.Messaging;
using Fleet.Policy.Safety;
using Fleet.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Connections.Tests;

public sealed class BrokerTests
{
    [Fact]
    public async Task Broker_denies_when_kill_switch_is_engaged_and_audits()
    {
        var audit = new InMemoryAuditLog();
        var kill = new KillSwitch(TimeProvider.System);
        kill.Engage("test");
        var broker = CreateBroker(audit, kill, out _);

        var result = await broker.InvokeAsync("ea", "gmail", "read_message", "{\"id\":\"1\"}", TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("deny", entry.Decision);
        Assert.Equal("broker_call", entry.Kind);
    }

    [Fact]
    public async Task Broker_allows_connector_redacts_and_audits_hash_size_taint()
    {
        var audit = new InMemoryAuditLog();
        var broker = CreateBroker(audit, new KillSwitch(TimeProvider.System), out var token);

        var result = await broker.InvokeAsync("ea", "gmail", "read_message", "{\"id\":\"1\"}", TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal(Taint.Tainted, result.Taint);
        Assert.DoesNotContain(token, result.Text, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("allow", entry.Decision);
        Assert.False(string.IsNullOrWhiteSpace(entry.ArgsHash));
        Assert.Contains("taint=Tainted", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("size=", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_endpoint_lists_only_granted_tools_and_rejects_wrong_token()
    {
        var provider = new InMemoryGrantProvider().Set("ea", [new Grant { Connection = "gmail", Operations = ["read_message"] }]);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IGrantProvider>(provider);
        builder.Services.AddSingleton(new PerAgentBearerTokenIssuer());
        builder.Services.AddSingleton(CreateBroker(new InMemoryAuditLog(), new KillSwitch(TimeProvider.System), out _));
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapFleetMcpEndpoints();
        var issuer = app.Services.GetRequiredService<PerAgentBearerTokenIssuer>();
        var token = issuer.Issue("ea");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/mcp/ea/tools", TestContext.Current.CancellationToken)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp/ea/tools");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gmail_read_message", json, StringComparison.Ordinal);
    }

    private static ConnectionBroker CreateBroker(InMemoryAuditLog audit, KillSwitch kill, out string token)
    {
        token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var store = new TestSecretStore();
        store.SetAsync("connection/gmail", token).AsTask().GetAwaiter().GetResult();
        var grants = new InMemoryGrantProvider().Set("ea", [new Grant { Connection = "gmail", Operations = ["read_message"], Constraints = new Dictionary<string, IReadOnlyList<string>> { ["id"] = ["*"] } }]);
        var registry = new InMemoryConnectorRegistry().Add(new StaticConnector("gmail"));
        return new ConnectionBroker(new GrantEnforcer(grants), new TokenVault(store), registry, audit, kill, new SecretRedactor());
    }

    private sealed class StaticConnector(string connectionId) : IConnector
    {
        public string ConnectionId { get; } = connectionId;

        public IReadOnlyList<string> Operations { get; } = ["read_message"];

        public ValueTask<Fleet.Connections.Model.BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default)
        {
            var text = $"message body contains {accessToken}";
            return ValueTask.FromResult(new Fleet.Connections.Model.BrokerResult(true, text, Taint.Tainted, text.Length));
        }
    }
}
