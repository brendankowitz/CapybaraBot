using System.Net.Http.Json;
using Capybara.Connections.Broker;
using Capybara.Connections.Health;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;

namespace Capybara.Connections.Client;

/// <summary>
/// capybarad's client for the separate capybara-broker process (FR-CON-01). The <see cref="HttpClient"/> carries the
/// broker base address and the broker API key. Every method throws <see cref="HttpRequestException"/> when the broker
/// is unreachable or refuses the request; callers decide whether that fails closed.
/// </summary>
/// <remarks>Thread-safe; register as a singleton over a factory-created client.</remarks>
public sealed class BrokerClient(HttpClient http)
{
    public async ValueTask<BrokerResult> InvokeAsync(BrokerCall call, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("/broker/invoke", call, BrokerJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrokerResult>(BrokerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("The broker returned an empty result.");
    }

    /// <summary>The broker tools the agent is granted, with input schemas (FR-CON-03).</summary>
    public async ValueTask<IReadOnlyList<BrokerToolDescriptor>> ListToolsAsync(string agentId, CancellationToken cancellationToken = default) =>
        await GetAsync<BrokerToolDescriptor[]>($"/broker/tools/{Uri.EscapeDataString(agentId)}", cancellationToken);

    public async ValueTask<IReadOnlyList<ConnectionStatus>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<ConnectionStatus[]>("/broker/connections", cancellationToken);

    /// <summary>Credential expiry metadata (no secrets) for the token health monitor (FR-CON-05).</summary>
    public async ValueTask<IReadOnlyList<CredentialHealth>> ListCredentialHealthAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<CredentialHealth[]>("/broker/credentials/health", cancellationToken);

    public ValueTask<BrokerHealth> GetHealthAsync(CancellationToken cancellationToken = default) => GetAsync<BrokerHealth>("/broker/health", cancellationToken);

    /// <summary>
    /// Denies every broker call until <see cref="UnfreezeAsync"/>; survives a broker restart (FR-SAF-06). Throws if the
    /// broker could not make the freeze durable or audit it; the broker is frozen in memory regardless, so callers
    /// should alert rather than retry blindly.
    /// </summary>
    /// <returns>How many provider requests were already in flight when the freeze landed (they cannot be recalled).</returns>
    public async ValueTask<FreezeOutcome> FreezeAsync(string reason, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("/broker/freeze", new BrokerFreezeRequest(reason), BrokerJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FreezeOutcome>(BrokerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("The broker returned no freeze outcome.");
    }

    public ValueTask UnfreezeAsync(CancellationToken cancellationToken = default) => PostAsync<object?>("/broker/unfreeze", null, cancellationToken);

    /// <summary>Drops an archived or deleted agent's grants and MCP bearer until the broker restarts (FR-AGT-07).</summary>
    public ValueTask RevokeAgentAsync(string agentId, CancellationToken cancellationToken = default) =>
        PostAsync<object?>($"/broker/agents/{Uri.EscapeDataString(agentId)}/revoke", null, cancellationToken);

    /// <summary>The agent's MCP path and bearer, for attaching the broker as an http MCP server to a session (FR-CON-03).</summary>
    public async ValueTask<McpAgentEndpoint> GetMcpEndpointAsync(string agentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"/broker/mcp/{Uri.EscapeDataString(agentId)}/token", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<McpAgentEndpoint>(BrokerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("The broker returned no MCP endpoint.");
    }

    /// <summary>Starts consent for a connection; show <see cref="InteractiveAuthRequest.AuthorizationUri"/> to the human (FR-CON-08).</summary>
    public async ValueTask<InteractiveAuthRequest> BeginAuthorizationAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"/broker/connections/{Uri.EscapeDataString(connectionId)}/authorize", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InteractiveAuthRequest>(BrokerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("The broker returned no authorization request.");
    }

    /// <summary>Forwards the provider redirect's <c>state</c> and <c>code</c> to the broker, which holds the PKCE verifier.</summary>
    public async ValueTask<ConnectionStatus> CompleteAuthorizationAsync(string state, string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("/broker/oauth/complete", new OAuthCompletion(state, code), BrokerJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConnectionStatus>(BrokerJson.Options, cancellationToken)
            ?? throw new HttpRequestException("The broker returned no connection status.");
    }

    private async ValueTask<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<T>(path, BrokerJson.Options, cancellationToken) ?? throw new HttpRequestException($"The broker returned an empty response for {path}.");

    private async ValueTask PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, body, BrokerJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
