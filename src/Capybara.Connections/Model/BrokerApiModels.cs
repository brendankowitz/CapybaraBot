using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Connections.Model;

/// <summary>Body of <c>POST /broker/freeze</c> (FR-SAF-06).</summary>
public sealed record BrokerFreezeRequest(string Reason);

/// <summary>Response of <c>GET /broker/health</c>.</summary>
public sealed record BrokerHealth(string Status, bool Frozen, string? FrozenReason);

/// <summary>Body of <c>POST /broker/oauth/complete</c>: what the provider redirect delivered (FR-CON-08).</summary>
public sealed record OAuthCompletion(string State, string Code);

/// <summary>
/// Response of <c>POST /broker/mcp/{agentId}/token</c>: the bearer and loopback path for the agent's MCP endpoint
/// (FR-CON-03).
/// </summary>
public sealed record McpAgentEndpoint(string Path, string Token)
{
    public override string ToString() => $"McpAgentEndpoint {{ Path = {Path} }}";
}

/// <summary>Wire format shared by the broker and <c>BrokerClient</c>: web defaults with enums as strings.</summary>
public static class BrokerJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
