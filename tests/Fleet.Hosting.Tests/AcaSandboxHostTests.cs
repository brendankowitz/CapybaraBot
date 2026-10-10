using Fleet.Hosting.Agents;

namespace Fleet.Hosting.Tests;

public sealed class AcaSandboxHostTests
{
    [Fact]
    public async Task Provision_request_defaults_to_deny_egress_with_transform_headers()
    {
        var handler = new FakeHttpMessageHandler();
        using var client = new HttpClient(handler);
        var host = new AcaSandboxHost(client, new FakeAzureCredential(), new AcaSandboxHostOptions(new Uri("https://management.azure.test/sub/"), "2026-10-01-preview"), TimeProvider.System);
        var spec = new AgentHostSpec("code", "/base", "/work", ["github.com"], 1024, "50%", "image:tag", new Dictionary<string, string>());

        await host.ProvisionAsync(spec, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("api-version=2026-10-01-preview", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        var body = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("defaultAction", body, StringComparison.Ordinal);
        Assert.Contains("Deny", body, StringComparison.Ordinal);
        Assert.Contains("addHeaders", body, StringComparison.Ordinal);
        Assert.Contains("github.com", body, StringComparison.Ordinal);
    }
}
