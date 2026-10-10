using System.Net;
using System.Security.Cryptography;
using Capybara.Connections.Connectors;
using Capybara.Connections.Model;
using Capybara.Contracts.Connections;
using Capybara.Contracts.Storage;

namespace Capybara.Connections.Tests;

/// <summary>FR-CON-10: no failure path escapes the audit log. Each case yields exactly one audited denial.</summary>
public sealed class BrokerFailureAuditTests
{
    private static readonly BrokerCall Read = new("ea", "email", "read", "{\"id\":\"1\"}");

    [Fact]
    public async Task Non_string_recipients_are_denied_and_audited()
    {
        var gmail = new GmailConnector(new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"))), ConnectionDefinition.Create("email", ConnectorKind.Gmail));
        using var h = new BrokerHarness(grants: [new Grant { Connection = "email", Operations = ["draft"] }], connector: gmail);
        await h.AuthorizeAsync();

        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "draft", """{"to":[1],"subject":"s","body":"b"}"""), TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, "invalid arguments: to must contain only strings");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public async Task Non_object_arguments_are_denied_by_the_grant_check(string args)
    {
        using var h = new BrokerHarness(grants: [new Grant { Connection = "email", Operations = ["read"], Constraints = new Dictionary<string, IReadOnlyList<string>> { ["id"] = ["*"] } }]);
        await h.AuthorizeAsync();

        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", args), TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, "arguments must be a JSON object");
    }

    [Theory]
    [InlineData("invalid_client", "credential refresh failed (invalid_client)")]
    [InlineData("server_error", "credential refresh failed (http_503)")]
    [InlineData("malformed", "credential refresh failed (JsonException)")]
    [InlineData("timeout", "credential refresh failed (timed out)")]
    [InlineData("unreachable", "credential refresh failed (unreachable")]
    [InlineData("missing_secret", "credential refresh failed (missing_client_secret)")]
    public async Task Refresh_failures_are_denied_and_audited(string failure, string reason)
    {
        using var h = new BrokerHarness(tokenEndpoint: _ => failure switch
        {
            "invalid_client" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"invalid_client"}""") },
            "server_error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>down</html>") },
            "malformed" => FakeHttpMessageHandler.Json("{not json"),
            "timeout" => throw new TaskCanceledException("timeout", new TimeoutException()),
            "unreachable" => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
            _ => throw new InvalidOperationException("token endpoint must not be called"),
        });
        if (failure == "missing_secret")
        {
            await h.Secrets.DeleteAsync(BrokerHarness.ClientSecretName, TestContext.Current.CancellationToken);
        }

        await h.AuthorizeExpiringAsync();

        var result = await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, reason);
    }

    [Fact]
    public async Task Undecryptable_vault_entry_is_denied_and_audited()
    {
        using var h = new BrokerHarness(secrets: new CorruptSecretStore());

        var result = await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, "credential failed (AuthenticationTagMismatchException)");
    }

    [Fact]
    public async Task Connector_timeout_is_audited_as_outcome_unknown()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        h.Connector.Behavior = (_, _) => throw new TaskCanceledException("timeout", new TimeoutException());

        var result = await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, "connector timed out; outcome unknown", connectorCalled: true);
    }

    [Fact]
    public async Task Unexpected_connector_exception_is_denied_and_audited_by_type()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        h.Connector.Behavior = (_, _) => throw new InvalidOperationException("secret-looking detail ya29.abcdefghijk");

        var result = await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        AssertDeniedOnce(h, result, "connector failed (InvalidOperationException)", connectorCalled: true);
        Assert.DoesNotContain("ya29", Assert.Single(h.BrokerCalls).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_audited_then_rethrown()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        using var cts = new CancellationTokenSource();
        h.Connector.Behavior = (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await h.Broker.InvokeAsync(Read, cts.Token));

        var entry = Assert.Single(h.BrokerCalls);
        Assert.Equal("deny", entry.Decision);
        Assert.Contains("cancelled by caller during connector; outcome unknown", entry.Detail, StringComparison.Ordinal);
    }

    private static void AssertDeniedOnce(BrokerHarness h, BrokerResult result, string reason, bool connectorCalled = false)
    {
        Assert.False(result.Allowed);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        var entry = Assert.Single(h.BrokerCalls);
        Assert.Equal(("deny", "ea"), (entry.Decision, entry.Agent));
        Assert.Contains(reason, entry.Detail, StringComparison.Ordinal);
        Assert.Equal(64, entry.ArgsHash!.Length);
        Assert.Equal(connectorCalled ? 1 : 0, h.Connector.Calls.Count);
    }

    private sealed class CorruptSecretStore : ISecretStore
    {
        public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default) => throw new AuthenticationTagMismatchException();

        public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
    }
}
