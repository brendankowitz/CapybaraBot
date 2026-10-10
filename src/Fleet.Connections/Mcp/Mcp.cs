using System.Security.Cryptography;
using System.Text;
using Fleet.Connections.Grants;
using Fleet.Connections.Broker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fleet.Connections.Mcp;

/// <summary>Issues random per-agent bearer tokens for loopback MCP endpoints (FR-CON-03).</summary>
public sealed class PerAgentBearerTokenIssuer
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Issue(string agentId)
    {
        lock (_gate)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _tokens[agentId] = token;
            return token;
        }
    }

    public bool Validate(string agentId, string? token)
    {
        lock (_gate)
        {
            return token is not null && _tokens.TryGetValue(agentId, out var expected) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
        }
    }
}

/// <summary>Loopback MCP façade; uses MCP 2.2 HTTP transport shape but routes through the broker (FR-CON-03).</summary>
public static class FleetMcpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapFleetMcpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/mcp/{agentId}");
        group.MapGet("/tools", (string agentId, HttpRequest request, PerAgentBearerTokenIssuer tokens, IGrantProvider grants) =>
        {
            if (!tokens.Validate(agentId, ReadBearer(request)))
            {
                return Results.Unauthorized();
            }

            var tools = grants.GetGrants(agentId).SelectMany(g => g.Operations.Select(o => new { name = $"{g.Connection}_{o}", connection = g.Connection, operation = o })).ToArray();
            return Results.Json(new { tools });
        });
        group.MapPost("/call", async (string agentId, HttpRequest request, McpToolCall call, PerAgentBearerTokenIssuer tokens, ConnectionBroker broker, CancellationToken ct) =>
        {
            if (!tokens.Validate(agentId, ReadBearer(request)))
            {
                return Results.Unauthorized();
            }

            var result = await broker.InvokeAsync(agentId, call.Connection, call.Operation, call.ArgsJson, ct);
            return Results.Json(result);
        });
        return endpoints;
    }

    private static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }
}

public sealed record McpToolCall(string Connection, string Operation, string ArgsJson);

/// <summary>Vendored third-party MCP server catalog with SHA-256 pin verification (FR-SKL-04).</summary>
public sealed class McpServerCatalog(IReadOnlyList<McpServerRegistration> servers)
{
    public IReadOnlyList<McpServerRegistration> Servers => servers;

    public async ValueTask VerifyAsync(McpServerRegistration server, CancellationToken cancellationToken = default)
    {
        _ = Servers.Count;
        await using var stream = File.OpenRead(server.ExecutablePath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, server.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MCP server hash mismatch for {server.Name}.");
        }
    }
}

public sealed record McpServerRegistration(string Name, string ExecutablePath, string Sha256, IReadOnlyList<string> Arguments);
