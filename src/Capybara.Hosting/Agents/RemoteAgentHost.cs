using System.Net.Http.Json;

namespace Capybara.Hosting.Agents;

/// <summary>Hybrid host that reaches on-prem agents over an outbound relay or tailnet endpoint (FR-HST-09).</summary>
public sealed class RemoteAgentHost(HttpClient httpClient, TimeProvider time) : IAgentHost
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly TimeProvider _time = time;

    public async ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        using var response = await _httpClient.PostAsJsonAsync($"agents/{spec.AgentId}/provision", spec, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default) => await PostAsync(agentId, "start", cancellationToken);

    public async ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default) => await PostAsync(agentId, "stop", cancellationToken);

    public async ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        var health = await _httpClient.GetFromJsonAsync<AgentHostHealth>($"agents/{agentId}/health", cancellationToken);
        return health ?? new AgentHostHealth(false, "unknown", _time.GetUtcNow());
    }

    public async ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        using var response = await _httpClient.PostAsJsonAsync($"agents/{agentId}/exec", new { command }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExecResult>(cancellationToken) ?? new ExecResult(1, string.Empty, "empty response");
    }

    public async ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        using var response = await _httpClient.PostAsync($"agents/{agentId}/snapshot", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SnapshotInfo>(cancellationToken) ?? new SnapshotInfo($"remote-{agentId}", string.Empty, _time.GetUtcNow());
    }

    private async ValueTask PostAsync(string agentId, string action, CancellationToken cancellationToken)
    {
        AgentIdValidator.Validate(agentId);
        using var response = await _httpClient.PostAsync($"agents/{agentId}/{action}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
