using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Fleet.Connections.Broker;
using Fleet.Connections.Connectors;
using Fleet.Connections.DependencyInjection;
using Fleet.Connections.Mcp;
using Fleet.Connections.Model;
using Fleet.Connections.Vault;
using Fleet.Contracts.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSystemd();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var brokerOptions = builder.Configuration.GetSection("Broker").Get<BrokerOptions>() ?? new BrokerOptions();
var credentialsDirectory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") ?? brokerOptions.CredentialsDirectory;
if (!string.IsNullOrWhiteSpace(credentialsDirectory))
{
    builder.Services.AddSingleton<ISecretStore>(new CredentialsDirectorySecretStore(credentialsDirectory));
}

builder.Services.AddFleetConnections(new ConnectionsOptions { CredentialsDirectory = credentialsDirectory });
builder.Services.AddSingleton(brokerOptions);
builder.WebHost.ConfigureKestrel(options =>
{
    if (!string.IsNullOrWhiteSpace(brokerOptions.UnixSocketPath))
    {
        options.ListenUnixSocket(brokerOptions.UnixSocketPath);
    }
    else
    {
        options.ListenLocalhost(brokerOptions.Port);
    }
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/broker") && !await BrokerApiAuthentication.IsAuthorizedAsync(context, credentialsDirectory, brokerOptions.ApiKey))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapFleetMcpEndpoints();
app.MapPost("/broker/invoke", async (BrokerCall call, ConnectionBroker broker, CancellationToken ct) => await broker.InvokeAsync(call.AgentId, call.Connection, call.Operation, call.ArgsJson, ct));
app.MapGet("/broker/connections", (IConnectorRegistry registry) => registry.List().Select(c => new ConnectionStatus(c.ConnectionId, Guess(c.ConnectionId), true, null)).ToArray());
app.MapGet("/broker/health", () => TypedResults.Json(new { status = "ok" }));

await app.RunAsync();

static ConnectionProviderKind Guess(string id) => id.Contains("github", StringComparison.OrdinalIgnoreCase) ? ConnectionProviderKind.GitHub : id.Contains("gmail", StringComparison.OrdinalIgnoreCase) ? ConnectionProviderKind.Gmail : ConnectionProviderKind.MicrosoftGraph;

internal sealed class BrokerOptions
{
    public int Port { get; set; } = 8788;

    public string? UnixSocketPath { get; set; }

    public string? CredentialsDirectory { get; set; }

    public string? ApiKey { get; set; }
}

internal static class BrokerApiAuthentication
{
    public static async ValueTask<bool> IsAuthorizedAsync(HttpContext context, string? credentialsDirectory, string? configuredKey)
    {
        var supplied = ReadBearer(context.Request);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var expected = configuredKey;
        if (string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(credentialsDirectory))
        {
            var path = Path.Combine(credentialsDirectory, "broker-api-key");
            expected = File.Exists(path) ? (await File.ReadAllTextAsync(path, context.RequestAborted)).Trim() : null;
        }

        return !string.IsNullOrWhiteSpace(expected) && FixedEquals(expected, supplied);
    }

    private static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }

    private static bool FixedEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

/// <summary>Entry point exposed for WebApplicationFactory tests.</summary>
public partial class Program;
