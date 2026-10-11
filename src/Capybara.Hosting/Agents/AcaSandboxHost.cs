using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;

namespace Capybara.Hosting.Agents;

/// <summary>
/// Azure Container Apps Sandbox host over the preview REST API (FR-HST-07). The API shape is intentionally configurable
/// and currently unverified; egress rules default-deny and Transform rules inject credentials outside the sandbox.
/// </summary>
public sealed class AcaSandboxHost(HttpClient httpClient, TokenCredential credential, AcaSandboxHostOptions options, TimeProvider time) : IAgentHost
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly TokenCredential _credential = credential;
    private readonly AcaSandboxHostOptions _options = options;
    private readonly TimeProvider _time = time;

    public async ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        await SendAsync(HttpMethod.Put, $"sandboxes/{spec.AgentId}", new
        {
            properties = new
            {
                image = spec.Image,
                workingDirectory = spec.WorkingDirectory,
                environment = spec.Environment,
                resources = new { memoryBytes = spec.MemoryMaxBytes, cpuQuota = spec.CpuQuota },
                egress = new
                {
                    defaultAction = "Deny",
                    rules = spec.EgressDomains.Select(domain => new
                    {
                        domain,
                        action = "Allow",
                        transform = new { addHeaders = new[] { new { name = "Authorization", valueFromSecretRef = "egress-token" } } },
                    }),
                },
            },
        }, cancellationToken);
    }

    public async ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await SendAsync(HttpMethod.Post, $"sandboxes/{agentId}/resume", null, cancellationToken);
    }

    public async ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await SendAsync(HttpMethod.Post, $"sandboxes/{agentId}/suspend", null, cancellationToken);
    }

    public async ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        using var response = await SendAsync(HttpMethod.Get, $"sandboxes/{agentId}", null, cancellationToken);
        var status = await response.Content.ReadFromJsonAsync<AcaSandboxStatus>(cancellationToken);
        return new AgentHostHealth(string.Equals(status?.Properties?.State, "Running", StringComparison.OrdinalIgnoreCase), status?.Properties?.State ?? "unknown", _time.GetUtcNow());
    }

    public async ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        ArgumentNullException.ThrowIfNull(command);
        using var response = await SendAsync(HttpMethod.Post, $"sandboxes/{agentId}/exec", new { command }, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AcaExecResponse>(cancellationToken);
        return new ExecResult(result?.ExitCode ?? 1, result?.Stdout ?? string.Empty, result?.Stderr ?? string.Empty);
    }

    public async ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        using var response = await SendAsync(HttpMethod.Post, $"sandboxes/{agentId}/snapshots", null, cancellationToken);
        var snapshot = await response.Content.ReadFromJsonAsync<AcaSnapshotResponse>(cancellationToken);
        return new SnapshotInfo(snapshot?.Id ?? $"aca-{agentId}-{_time.GetUtcNow():yyyyMMddHHmmss}", snapshot?.Uri ?? string.Empty, _time.GetUtcNow());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext([_options.Scope]), cancellationToken);
        var builder = new UriBuilder(new Uri(_options.BaseUri, relativePath)) { Query = $"api-version={Uri.EscapeDataString(_options.ApiVersion)}" };
        var request = new HttpRequestMessage(method, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed record AcaSandboxStatus(AcaSandboxProperties? Properties);

    private sealed record AcaSandboxProperties(string? State);

    private sealed record AcaExecResponse(int ExitCode, string? Stdout, string? Stderr);

    private sealed record AcaSnapshotResponse(string? Id, string? Uri);
}

/// <summary>Configurable base URL and API version for the unverified ACA Sandbox preview API (FR-HST-07).</summary>
public sealed record AcaSandboxHostOptions(Uri BaseUri, string ApiVersion)
{
    public string Scope { get; init; } = "https://management.azure.com/.default";
}
