using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fleet.Broker.Tests;

public sealed class BrokerApiAuthTests
{
    [Fact]
    public async Task Broker_api_rejects_requests_without_shared_key()
    {
        using var fixture = new CredentialFixture();
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/broker/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Broker_api_accepts_constant_time_bearer_key()
    {
        using var fixture = new CredentialFixture();
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/broker/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", fixture.ApiKey);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class CredentialFixture : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");

        public CredentialFixture()
        {
            Root = Path.Combine(Environment.CurrentDirectory, "broker-test-credentials", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "broker-api-key"), ApiKey);
            Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", Root);
        }

        public string ApiKey { get; } = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        public string Root { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", _previous);
            Directory.Delete(Root, recursive: true);
        }
    }
}
