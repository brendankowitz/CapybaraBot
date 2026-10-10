using Capybara.Connections.Connectors;
using Capybara.Contracts.Messaging;

namespace Capybara.Connections.Tests;

public sealed class ConnectorTests
{
    [Fact]
    public async Task Gmail_read_uses_bearer_and_marks_output_tainted()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{\"id\":\"msg\"}"));
        var connector = new GmailConnector(new HttpClient(handler));
        using var args = System.Text.Json.JsonDocument.Parse("{\"id\":\"msg\"}");

        var result = await connector.InvokeAsync("read_message", args.RootElement, "runtime-token", TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal(Taint.Tainted, result.Taint);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.EndsWith("/messages/msg", request.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }
}
