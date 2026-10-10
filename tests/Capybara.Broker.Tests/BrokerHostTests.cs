using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Capybara.Connections.Model;
using Capybara.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace Capybara.Broker.Tests;

/// <summary>The capybara-broker process end to end, started with the sample config (FR-CON-01..10).</summary>
public sealed class BrokerHostTests
{
    [Fact]
    public async Task Broker_api_requires_the_shared_key()
    {
        using var dirs = new BrokerDirectories();
        await using var factory = new BrokerFactory(dirs);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/broker/health", ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/broker/health", ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dirs.ApiKey);
        var health = await client.GetFromJsonAsync<BrokerHealth>("/broker/health", BrokerJson.Options, ct);
        Assert.Equal(("ok", false), (health!.Status, health.Frozen));
    }

    [Fact]
    public async Task Startup_loads_grants_from_agent_yaml()
    {
        using var dirs = new BrokerDirectories();
        await using var factory = new BrokerFactory(dirs);
        using var client = dirs.Authorize(factory.CreateClient());
        var ct = TestContext.Current.CancellationToken;

        var ea = await client.GetFromJsonAsync<BrokerToolDescriptor[]>("/broker/tools/ea", BrokerJson.Options, ct);
        var research = await client.GetFromJsonAsync<BrokerToolDescriptor[]>("/broker/tools/research", BrokerJson.Options, ct);
        var code = await client.GetFromJsonAsync<BrokerToolDescriptor[]>("/broker/tools/code", BrokerJson.Options, ct);

        Assert.Equal(["calendar_create", "calendar_list", "calendar_read", "email_draft", "email_list", "email_read", "email_send"], ea!.Select(t => t.Name));
        Assert.Empty(research!);
        Assert.Contains(code!, t => t.Name == "github_create_pr");
    }

    [Fact]
    public async Task Freeze_denies_calls_is_audited_and_survives_restart()
    {
        using var dirs = new BrokerDirectories();
        var ct = TestContext.Current.CancellationToken;
        await using (var factory = new BrokerFactory(dirs))
        {
            using var client = dirs.Authorize(factory.CreateClient());
            (await client.PostAsJsonAsync("/broker/freeze", new BrokerFreezeRequest("kill switch"), BrokerJson.Options, ct)).EnsureSuccessStatusCode();

            var result = await InvokeAsync(client, new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"));

            Assert.False(result.Allowed);
            Assert.Contains("frozen", result.Reason, StringComparison.Ordinal);
        }

        await using (var restarted = new BrokerFactory(dirs))
        {
            using var client = dirs.Authorize(restarted.CreateClient());
            Assert.True((await client.GetFromJsonAsync<BrokerHealth>("/broker/health", BrokerJson.Options, ct))!.Frozen);
            (await client.PostAsync("/broker/unfreeze", content: null, ct)).EnsureSuccessStatusCode();
            Assert.False((await client.GetFromJsonAsync<BrokerHealth>("/broker/health", BrokerJson.Options, ct))!.Frozen);
        }

        var audit = await File.ReadAllTextAsync(Path.Combine(dirs.State, "audit", "broker-audit.jsonl"), ct);
        Assert.Contains("broker_freeze", audit, StringComparison.Ordinal);
        Assert.Contains("broker_call", audit, StringComparison.Ordinal);
        Assert.Contains("broker_unfreeze", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_connection_is_denied_and_reported()
    {
        using var dirs = new BrokerDirectories();
        await using var factory = new BrokerFactory(dirs);
        using var client = dirs.Authorize(factory.CreateClient());
        var ct = TestContext.Current.CancellationToken;

        var result = await InvokeAsync(client, new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"));
        var connections = await client.GetFromJsonAsync<ConnectionStatus[]>("/broker/connections", BrokerJson.Options, ct);

        Assert.Equal("connection is not authorized", result.Reason);
        Assert.All(connections!, c => Assert.False(c.Authorized));
        Assert.Equal(["calendar", "email", "github"], connections!.Select(c => c.Id));
    }

    [Fact]
    public async Task Mcp_endpoint_lists_the_agents_granted_tools()
    {
        using var dirs = new BrokerDirectories();
        await using var factory = new BrokerFactory(dirs);
        using var api = dirs.Authorize(factory.CreateClient());
        var ct = TestContext.Current.CancellationToken;
        var endpoint = await (await api.PostAsync("/broker/mcp/research/token", content: null, ct)).Content.ReadFromJsonAsync<McpAgentEndpoint>(BrokerJson.Options, ct);
        var codeEndpoint = await (await api.PostAsync("/broker/mcp/code/token", content: null, ct)).Content.ReadFromJsonAsync<McpAgentEndpoint>(BrokerJson.Options, ct);

        await using var research = await ConnectAsync(factory, endpoint!, ct);
        await using var code = await ConnectAsync(factory, codeEndpoint!, ct);

        Assert.Empty(await research.ListToolsAsync(cancellationToken: ct));
        Assert.Equal(6, (await code.ListToolsAsync(cancellationToken: ct)).Count);
    }

    [Fact]
    public async Task Grant_naming_drift_fails_startup_closed()
    {
        using var dirs = new BrokerDirectories();
        var agent = Directory.CreateDirectory(Path.Combine(dirs.Config, "agents", "ea")).FullName;
        await File.WriteAllTextAsync(Path.Combine(agent, "agent.yaml"), "id: ea\ngrants:\n  - connection: gmail\n    operations: [read]\n", TestContext.Current.CancellationToken);
        await using var factory = new BrokerFactory(dirs, configRoot: dirs.Config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("'gmail'", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_vault_key_fails_startup_closed()
    {
        using var dirs = new BrokerDirectories();
        File.Delete(Path.Combine(dirs.Credentials, "broker-vault-key"));
        await using var factory = new BrokerFactory(dirs);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("broker-vault-key", ex.ToString(), StringComparison.Ordinal);
    }

    private static async Task<BrokerResult> InvokeAsync(HttpClient client, BrokerCall call)
    {
        var response = await client.PostAsJsonAsync("/broker/invoke", call, BrokerJson.Options, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BrokerResult>(BrokerJson.Options, TestContext.Current.CancellationToken))!;
    }

    private static async Task<McpClient> ConnectAsync(BrokerFactory factory, McpAgentEndpoint endpoint, CancellationToken ct)
    {
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, endpoint.Path),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {endpoint.Token}" },
            },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private sealed class BrokerFactory(BrokerDirectories dirs, string? configRoot = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Broker:ConfigRoot", configRoot ?? RepositoryPaths.SampleConfig);
            builder.UseSetting("Broker:StateDirectory", dirs.State);
            builder.UseSetting("Broker:CredentialsDirectory", dirs.Credentials);
        }
    }

    private sealed class BrokerDirectories : IDisposable
    {
        private readonly TempDirectory _root = new();

        public BrokerDirectories()
        {
            Directory.CreateDirectory(Credentials);
            Directory.CreateDirectory(State);
            File.WriteAllText(Path.Combine(Credentials, "broker-api-key"), ApiKey);
            File.WriteAllText(Path.Combine(Credentials, "broker-vault-key"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        public string ApiKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        public string Credentials => Path.Combine(_root.Path, "credentials");

        public string State => Path.Combine(_root.Path, "state");

        public string Config => Path.Combine(_root.Path, "config");

        public HttpClient Authorize(HttpClient client)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            return client;
        }

        public void Dispose() => _root.Dispose();
    }
}
