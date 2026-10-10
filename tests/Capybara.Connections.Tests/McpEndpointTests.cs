using System.Net;
using System.Text.Json;
using Capybara.Connections.Mcp;
using Capybara.Contracts.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Capybara.Connections.Tests;

/// <summary>The per-agent endpoint speaks real MCP (FR-CON-03), exercised with the MCP C# SDK client.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private readonly BrokerHarness _harness = new(grants: [new Grant { Connection = "email", Operations = ["read", "list"] }]);

    private WebApplication? _app;
    private Uri? _address;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_harness.Broker);
        builder.Services.AddSingleton(_harness.Catalog);
        builder.Services.AddCapybaraBrokerMcp();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.MapCapybaraMcpEndpoints();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        await _harness.AuthorizeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _harness.Dispose();
    }

    [Fact]
    public async Task Tools_list_returns_only_granted_tools_with_schemas()
    {
        await using var client = await ConnectAsync("ea", Token("ea"));

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["email_list", "email_read"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        var read = tools.Single(t => t.Name == "email_read");
        Assert.Equal("id", read.JsonSchema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Tools_call_runs_through_the_broker_and_reports_taint()
    {
        await using var client = await ConnectAsync("ea", Token("ea"));

        var result = await client.CallToolAsync("email_read", new Dictionary<string, object?> { ["id"] = "42" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Tainted", result.Meta?[BrokerMcpServer.TaintMetaKey]?.GetValue<string>());
        Assert.Contains("message body", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
        var call = Assert.Single(_harness.Connector.Calls);
        Assert.Equal("read", call.Operation);
        Assert.Equal("email_read", Assert.Single(_harness.Audit.Entries).Tool);
    }

    [Fact]
    public async Task Ungranted_tool_call_is_an_error_result()
    {
        await using var client = await ConnectAsync("ea", Token("ea"));

        var result = await client.CallToolAsync("email_send", new Dictionary<string, object?> { ["to"] = "x@example.com" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(_harness.Connector.Calls);
        var entry = Assert.Single(_harness.Audit.Entries);
        Assert.Equal(("broker_call", "deny", "ea", "email_send"), (entry.Kind, entry.Decision, entry.Agent, entry.Tool));
        Assert.Contains("tool is not granted", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(64, entry.ArgsHash!.Length);
    }

    [Fact]
    public async Task Another_agents_token_or_no_token_is_rejected()
    {
        var eaToken = Token("ea");
        _ = Token("research");
        using var http = new HttpClient { BaseAddress = _address };
        var ct = TestContext.Current.CancellationToken;

        using var anonymous = await http.PostAsync("/mcp/ea", JsonBody(), ct);
        using var wrongAgent = new HttpRequestMessage(HttpMethod.Post, "/mcp/research") { Content = JsonBody() };
        wrongAgent.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", eaToken);
        using var crossed = await http.SendAsync(wrongAgent, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);
    }

    private string Token(string agent) => _app!.Services.GetRequiredService<PerAgentBearerTokenIssuer>().GetOrIssue(agent);

    private async Task<McpClient> ConnectAsync(string agent, string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_address!, $"/mcp/{agent}"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static StringContent JsonBody() =>
        new(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" }), System.Text.Encoding.UTF8, "application/json");
}
