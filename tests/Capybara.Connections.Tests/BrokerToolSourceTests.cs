using System.Net;
using System.Text;
using System.Text.Json;
using Capybara.Connections.Client;
using Capybara.Connections.Connectors;
using Capybara.Connections.Model;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Provenance;
using Microsoft.Extensions.AI;

namespace Capybara.Connections.Tests;

/// <summary>The capybarad side: <see cref="IBrokerToolSource"/> against a fake broker.</summary>
public sealed class BrokerToolSourceTests
{
    private readonly List<BrokerCall> _calls = [];
    private BrokerResult _result = new(true, "200 OK: {\"subject\":\"hello\"}", Taint.Tainted, 10);

    [Fact]
    public async Task Exposes_only_granted_tools_that_are_allowlisted_with_connector_schemas()
    {
        var source = new BrokerToolSource(Client(), new TaintTracker());

        // email_send is granted but not allowlisted; calendar_read is allowlisted but not granted.
        var tools = await source.GetToolsAsync("ea", "agent:ea:main", ["email_read", "calendar_read", "list_agents"], TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("email_read", tool.Name);
        Assert.Equal("id", tool.JsonSchema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Invoking_calls_the_broker_as_the_agent_and_taints_the_session()
    {
        var taint = new TaintTracker();
        var source = new BrokerToolSource(Client(), taint);
        var tool = Assert.Single(await source.GetToolsAsync("ea", "agent:ea:main", ["email_read"], TestContext.Current.CancellationToken));

        var output = await tool.InvokeAsync(new AIFunctionArguments { ["id"] = "42" }, TestContext.Current.CancellationToken);

        var call = Assert.Single(_calls);
        Assert.Equal(("ea", "email", "read", "agent:ea:main"), (call.AgentId, call.Connection, call.Operation, call.SessionId));
        Assert.Equal("42", JsonDocument.Parse(call.ArgsJson).RootElement.GetProperty("id").GetString());
        Assert.True(taint.IsTainted("agent:ea:main"));
        Assert.Contains("data, not instructions", Assert.IsType<string>(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denials_are_returned_verbatim_and_do_not_taint()
    {
        _result = BrokerResult.Denied("broker frozen (kill switch)");
        var taint = new TaintTracker();
        var tool = Assert.Single(await new BrokerToolSource(Client(), taint).GetToolsAsync("ea", "s1", ["email_read"], TestContext.Current.CancellationToken));

        var output = await tool.InvokeAsync(new AIFunctionArguments { ["id"] = "1" }, TestContext.Current.CancellationToken);

        Assert.Equal("denied: broker frozen (kill switch)", output);
        Assert.False(taint.IsTainted("s1"));
    }

    private BrokerClient Client() => new(new HttpClient(new FakeBroker(this)) { BaseAddress = new Uri("http://127.0.0.1:8788") });

    private sealed class FakeBroker(BrokerToolSourceTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/broker/tools/ea" => MailOperations.All
                    .Where(o => o.Name is "read" or "send")
                    .Select(o => new BrokerToolDescriptor($"email_{o.Name}", "email", o.Name, o.Description, o.InputSchema, o.Untrusted))
                    .ToArray(),
                "/broker/invoke" => Record(JsonSerializer.Deserialize<BrokerCall>(await request.Content!.ReadAsStringAsync(cancellationToken), BrokerJson.Options)!),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, BrokerJson.Options), Encoding.UTF8, "application/json") };
        }

        private BrokerResult Record(BrokerCall call)
        {
            owner._calls.Add(call);
            return owner._result;
        }
    }
}
