using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fleet.Client;

/// <summary>Typed HTTP client for fleet gateway routes (FR-CLI-02, FR-MEM-12, FR-RTN-09).</summary>
public sealed class FleetApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentStatusDto>> GetAgentsAsync(CancellationToken cancellationToken = default) => GetListAsync<AgentStatusDto>("/api/agents", cancellationToken);

    public Task PauseAgentAsync(string id, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/agents/{Uri.EscapeDataString(id)}/pause", null, cancellationToken);

    public Task ResumeAgentAsync(string id, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/agents/{Uri.EscapeDataString(id)}/resume", null, cancellationToken);

    public async Task<TurnResultDto?> SendTurnAsync(string id, string prompt, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync($"/api/agents/{Uri.EscapeDataString(id)}/turn", new TurnRequestDto(prompt), JsonOptions, cancellationToken);
        return await ReadOrDefaultAsync<TurnResultDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<RuntimeStatusDto>> GetRuntimesAsync(CancellationToken cancellationToken = default) => GetListAsync<RuntimeStatusDto>("/api/runtimes", cancellationToken);

    public Task UnquarantineRuntimeAsync(string id, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/runtimes/{Uri.EscapeDataString(id)}/unquarantine", null, cancellationToken);

    public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(CancellationToken cancellationToken = default) => GetListAsync<ApprovalDto>("/api/approvals", cancellationToken);

    public Task DecideApprovalAsync(string id, string nonce, string decision, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/approvals/{Uri.EscapeDataString(id)}", new ApprovalDecisionDto(nonce, decision), cancellationToken);

    public Task<KillSwitchDto?> GetKillSwitchAsync(CancellationToken cancellationToken = default) => GetAsync<KillSwitchDto>("/api/killswitch", cancellationToken);

    public Task EngageKillSwitchAsync(string reason, CancellationToken cancellationToken = default) => PostEmptyAsync("/api/killswitch", new KillSwitchRequestDto(reason), cancellationToken);

    public async Task<bool> ReleaseKillSwitchAsync(CancellationToken cancellationToken = default)
    {
        var response = await http.DeleteAsync(new Uri("/api/killswitch", UriKind.Relative), cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public Task<AuditVerifyDto?> VerifyAuditAsync(CancellationToken cancellationToken = default) => GetAsync<AuditVerifyDto>("/api/audit/verify", cancellationToken);

    public Task<LedgerDto?> GetLedgerAsync(CancellationToken cancellationToken = default) => GetAsync<LedgerDto>("/api/ledger", cancellationToken);

    public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(CancellationToken cancellationToken = default) => GetListAsync<AlertDto>("/api/alerts", cancellationToken);

    public Task<IReadOnlyList<ThreadEntryDto>> GetThreadAsync(CancellationToken cancellationToken = default) => GetListAsync<ThreadEntryDto>("/api/thread", cancellationToken);

    public Task<IReadOnlyList<PendingMemoryChangeDto>> GetPendingMemoryAsync(CancellationToken cancellationToken = default) => GetListAsync<PendingMemoryChangeDto>("/api/memory/pending", cancellationToken);

    public Task DecidePendingMemoryAsync(string id, bool apply, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/memory/pending/{Uri.EscapeDataString(id)}", new PendingChangeDecisionDto(apply), cancellationToken);

    public Task<IReadOnlyList<MemoryPageInfoDto>> GetMemoryPagesAsync(string? prefix = null, CancellationToken cancellationToken = default) => GetListAsync<MemoryPageInfoDto>($"/api/memory/pages?prefix={Uri.EscapeDataString(prefix ?? string.Empty)}", cancellationToken);

    public Task<MemoryPageDto?> GetMemoryPageAsync(string path, CancellationToken cancellationToken = default) => GetAsync<MemoryPageDto>($"/api/memory/page?path={Uri.EscapeDataString(path)}", cancellationToken);

    public Task SaveMemoryPageAsync(string path, string content, CancellationToken cancellationToken = default) => PutEmptyAsync("/api/memory/page", new SaveMemoryPageDto(path, content), cancellationToken);

    public async Task<bool> DeleteMemoryPageAsync(string path, CancellationToken cancellationToken = default)
    {
        var response = await http.DeleteAsync(new Uri($"/api/memory/page?path={Uri.EscapeDataString(path)}", UriKind.Relative), cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    public Task<Stream?> ExportMemoryAsync(CancellationToken cancellationToken = default) => GetStreamOrNullAsync("/api/memory/export", cancellationToken);

    public Task<IReadOnlyList<RoutineDto>> GetRoutinesAsync(CancellationToken cancellationToken = default) => GetListAsync<RoutineDto>("/api/routines", cancellationToken);

    public Task EnableRoutineAsync(string id, decimal acknowledgedMonthlyCredits, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/routines/{Uri.EscapeDataString(id)}/enable", new EnableRoutineDto(acknowledgedMonthlyCredits), cancellationToken);

    public Task DisableRoutineAsync(string id, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/routines/{Uri.EscapeDataString(id)}/disable", null, cancellationToken);

    public Task DryRunRoutineAsync(string id, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/routines/{Uri.EscapeDataString(id)}/dry-run", null, cancellationToken);

    public Task<BudgetsDto?> GetBudgetsAsync(CancellationToken cancellationToken = default) => GetAsync<BudgetsDto>("/api/budgets", cancellationToken);

    public Task<IReadOnlyList<ProposalDto>> GetProposalsAsync(CancellationToken cancellationToken = default) => GetListAsync<ProposalDto>("/api/proposals", cancellationToken);

    public Task DecideProposalAsync(string id, bool approve, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/proposals/{Uri.EscapeDataString(id)}", new ProposalDecisionDto(approve), cancellationToken);

    public Task SetPresenceAsync(bool focused, CancellationToken cancellationToken = default) => PostEmptyAsync("/api/presence", new PresenceDto(focused), cancellationToken);

    public Task AnswerQuestionAsync(string id, string answer, CancellationToken cancellationToken = default) => PostEmptyAsync($"/api/questions/{Uri.EscapeDataString(id)}", new QuestionAnswerDto(answer), cancellationToken);

    public Task<IReadOnlyList<PushSubscriptionDto>> GetPushSubscriptionsAsync(CancellationToken cancellationToken = default) => GetListAsync<PushSubscriptionDto>("/api/push/subscriptions", cancellationToken);

    public Task AddPushSubscriptionAsync(PushSubscriptionDto subscription, CancellationToken cancellationToken = default) => PostEmptyAsync("/api/push/subscriptions", subscription, cancellationToken);

    public Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken = default) => GetListAsync<ConnectionDto>("/api/connections", cancellationToken);

    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(CancellationToken cancellationToken = default) => GetListAsync<TaskDto>("/api/tasks", cancellationToken);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken)
    {
        var value = await GetAsync<List<T>>(path, cancellationToken);
        return value ?? [];
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return await ReadOrDefaultAsync<T>(response, cancellationToken);
    }

    private async Task<Stream?> GetStreamOrNullAsync(string path, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private async Task PostEmptyAsync(string path, object? body, CancellationToken cancellationToken)
    {
        var response = body is null
            ? await http.PostAsync(new Uri(path, UriKind.Relative), null, cancellationToken)
            : await http.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);
        EnsureSuccessOrNotFound(response);
    }

    private async Task PutEmptyAsync(string path, object body, CancellationToken cancellationToken)
    {
        var response = await http.PutAsJsonAsync(path, body, JsonOptions, cancellationToken);
        EnsureSuccessOrNotFound(response);
    }

    private static async Task<T?> ReadOrDefaultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private static void EnsureSuccessOrNotFound(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}
