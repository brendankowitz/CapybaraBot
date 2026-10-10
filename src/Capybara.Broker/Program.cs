using System.Security.Cryptography;
using System.Text;
using Capybara.Connections.Broker;
using Capybara.Connections.DependencyInjection;
using Capybara.Connections.Mcp;
using Capybara.Connections.Model;

// capybara-broker (FR-CON-01): the only process that holds connection credentials. It listens on loopback (or a Unix
// socket) and serves capybarad's /broker API plus the per-agent MCP endpoints.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSystemd();
builder.Services.ConfigureHttpJsonOptions(o => BrokerJson.Configure(o.SerializerOptions));
var brokerOptions = builder.Configuration.GetSection(BrokerOptions.Section).Get<BrokerOptions>() ?? new BrokerOptions();
builder.Services.AddCapybaraConnectionBroker(brokerOptions);
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
    if (context.Request.Path.StartsWithSegments("/broker") && !BrokerApiAuthentication.IsAuthorized(context.Request, brokerOptions.ReadApiKey()))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next(context);
});

app.MapCapybaraBrokerApi();
app.MapCapybaraMcpEndpoints();

await app.RunAsync();

/// <summary>The /broker API is for capybarad only: it must present the shared broker API key (constant-time compare).</summary>
internal static class BrokerApiAuthentication
{
    public static bool IsAuthorized(HttpRequest request, string? expected)
    {
        var header = request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

/// <summary>Entry point exposed for WebApplicationFactory tests.</summary>
public partial class Program;
