using Capybara.Connections.Connectors;
using Capybara.Connections.Health;
using Capybara.Connections.Mcp;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Connections.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Capybara.Connections.Broker;

/// <summary>
/// The broker's loopback HTTP API, called only by capybarad with the broker API key (enforced by the host's
/// middleware for every <c>/broker</c> path). Route contract, consumed by <c>BrokerClient</c>:
/// <list type="bullet">
/// <item><c>POST /broker/invoke</c> <see cref="BrokerCall"/> → <see cref="BrokerResult"/></item>
/// <item><c>GET /broker/tools/{agentId}</c> → <see cref="BrokerToolDescriptor"/>[]</item>
/// <item><c>GET /broker/connections</c> → <see cref="ConnectionStatus"/>[]</item>
/// <item><c>GET /broker/credentials/health</c> → <see cref="CredentialHealth"/>[] (metadata only)</item>
/// <item><c>POST /broker/connections/{id}/authorize</c> → <see cref="InteractiveAuthRequest"/></item>
/// <item><c>POST /broker/oauth/complete</c> <see cref="OAuthCompletion"/> → <see cref="ConnectionStatus"/></item>
/// <item><c>POST /broker/freeze</c> <see cref="BrokerFreezeRequest"/>, <c>POST /broker/unfreeze</c> → 204</item>
/// <item><c>POST /broker/mcp/{agentId}/token</c> → <see cref="McpAgentEndpoint"/></item>
/// <item><c>GET /broker/health</c> → <see cref="BrokerHealth"/></item>
/// </list>
/// </summary>
public static class BrokerApiEndpoints
{
    public static IEndpointRouteBuilder MapCapybaraBrokerApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/broker");
        api.MapPost("/invoke", (BrokerCall call, ConnectionBroker broker, CancellationToken ct) => broker.InvokeAsync(call, ct));
        api.MapGet("/tools/{agentId}", (string agentId, BrokerToolCatalog catalog) => catalog.ForAgent(agentId));
        api.MapGet("/connections", ListConnectionsAsync);
        api.MapGet("/credentials/health", (ICredentialHealthSource health, CancellationToken ct) => health.ListAsync(ct));
        api.MapPost("/connections/{id}/authorize", (string id, ConnectorRegistry registry, CredentialManager credentials) =>
        {
            if (registry.Find(id) is not { } connector)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(credentials.BeginAuthorization(connector.Connection));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        api.MapPost("/oauth/complete", async (OAuthCompletion completion, CredentialManager credentials, TokenVault vault, CancellationToken ct) =>
        {
            try
            {
                var connection = await credentials.CompleteAuthorizationAsync(completion.State, completion.Code, ct);
                return Results.Ok(await StatusAsync(connection, vault, ct));
            }
            catch (OAuthException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: ex.Error);
            }
        });
        api.MapPost("/freeze", async (BrokerFreezeRequest request, BrokerFreeze freeze, CancellationToken ct) =>
        {
            await freeze.FreezeAsync(request.Reason, ct);
            return Results.NoContent();
        });
        api.MapPost("/unfreeze", async (BrokerFreeze freeze, CancellationToken ct) =>
        {
            await freeze.UnfreezeAsync(ct);
            return Results.NoContent();
        });
        api.MapPost("/mcp/{agentId}/token", (string agentId, PerAgentBearerTokenIssuer tokens) =>
            new McpAgentEndpoint($"/mcp/{Uri.EscapeDataString(agentId)}", tokens.GetOrIssue(agentId)));
        api.MapGet("/health", (BrokerFreeze freeze) => new BrokerHealth("ok", freeze.IsFrozen, freeze.Reason));
        return endpoints;
    }

    private static async Task<ConnectionStatus[]> ListConnectionsAsync(ConnectorRegistry registry, TokenVault vault, CancellationToken ct)
    {
        var statuses = new List<ConnectionStatus>();
        foreach (var connector in registry.List())
        {
            statuses.Add(await StatusAsync(connector.Connection, vault, ct));
        }

        return [.. statuses];
    }

    private static async Task<ConnectionStatus> StatusAsync(ConnectionDefinition connection, TokenVault vault, CancellationToken ct)
    {
        var credential = await vault.GetAsync(connection.Id, ct);
        return credential is null
            ? new ConnectionStatus(connection.Id, connection.Kind, connection.Provider, false, null, "not authorized")
            : new ConnectionStatus(connection.Id, connection.Kind, connection.Provider, true, credential.AccessTokenExpiresAt, credential.RefreshToken is null ? "no refresh token" : null);
    }
}
