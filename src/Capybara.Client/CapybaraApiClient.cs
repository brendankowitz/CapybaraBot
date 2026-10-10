using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Capybara.Client;

/// <summary>
/// Typed HTTP client for the capybarad gateway (FR-CLI-02, FR-CLI-05, FR-MEM-12, FR-RTN-09). The route table, DTOs and
/// auth surfaces are specified in <c>README-contract.md</c>; the gateway implements exactly those routes.
/// </summary>
/// <remarks>
/// Every non-success response throws <see cref="HttpRequestException"/> with <see cref="HttpRequestException.StatusCode"/>
/// set and the response body appended to the message. A 404 is never mapped to an empty result, so a missing route or a
/// missing item is visible to the caller (for example the questions page reports an expired question).
/// </remarks>
public sealed class CapybaraApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Id(string id) => Uri.EscapeDataString(id);

    // ---- Agents ---------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<AgentStatusDto>> GetAgentsAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<AgentStatusDto>>("/api/agents", cancellationToken);

    public Task PauseAgentAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/agents/{Id(id)}/pause", null, cancellationToken);

    public Task ResumeAgentAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/agents/{Id(id)}/resume", null, cancellationToken);

    public Task<TurnResultDto> SendTurnAsync(string id, string prompt, CancellationToken cancellationToken = default) => SendTurnAsync(id, new TurnRequestDto(prompt), cancellationToken);

    /// <summary>Sends a user turn; with <see cref="TurnRequestDto.Incognito"/> the session reads and writes no memory.</summary>
    public Task<TurnResultDto> SendTurnAsync(string id, TurnRequestDto request, CancellationToken cancellationToken = default) => PostForAsync<TurnResultDto>($"/api/agents/{Id(id)}/turn", request, cancellationToken);

    /// <summary>Marks the agent's messages as seen, clearing its unread count.</summary>
    public Task MarkAgentReadAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/agents/{Id(id)}/read", null, cancellationToken);

    public Task SetModelAsync(string id, string model, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, $"/api/agents/{Id(id)}/model", new SetModelDto(model), cancellationToken);

    public Task<IReadOnlyList<ChatEntryDto>> GetChatHistoryAsync(string id, int limit = 100, CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<ChatEntryDto>>($"/api/agents/{Id(id)}/history?limit={limit}", cancellationToken);

    /// <summary>
    /// Waits for the reply to a turn the server answered with <c>Accepted</c>, polling the chat history. Correlation is by
    /// <paramref name="messageId"/> (see <see cref="ChatReplies.To"/>); only when the server gave none does it fall back to
    /// <paramref name="snapshotLast"/>, the last history entry captured before the turn was sent (<see cref="ChatReplies.AfterSnapshot"/>).
    /// Returns an empty list if no reply appeared within <paramref name="maxPolls"/> polls.
    /// </summary>
    public async Task<IReadOnlyList<ChatEntryDto>> AwaitReplyAsync(string agentId, string? messageId, ChatEntryDto? snapshotLast, TimeSpan pollInterval, int maxPolls, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            await Task.Delay(pollInterval, cancellationToken);
            var replies = ChatReplies.Find(await GetChatHistoryAsync(agentId, 100, cancellationToken), messageId, snapshotLast);
            if (replies.Count > 0)
            {
                return replies;
            }
        }

        return [];
    }

    public Task<IReadOnlyList<RuntimeStatusDto>> GetRuntimesAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<RuntimeStatusDto>>("/api/runtimes", cancellationToken);

    public Task UnquarantineRuntimeAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/runtimes/{Id(id)}/unquarantine", null, cancellationToken);

    // ---- Lifecycle (desktop only) ---------------------------------------------------------------------------------

    public Task<IReadOnlyList<string>> GetAgentTemplatesAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<string>>("/api/lifecycle/templates", cancellationToken);

    public Task<LifecycleResultDto> CreateAgentAsync(CreateAgentDto request, CancellationToken cancellationToken = default) => PostForAsync<LifecycleResultDto>("/api/agents", request, cancellationToken);

    public Task<LifecycleResultDto> DuplicateAgentAsync(string source, DuplicateAgentDto request, CancellationToken cancellationToken = default) => PostForAsync<LifecycleResultDto>($"/api/agents/{Id(source)}/duplicate", request, cancellationToken);

    public Task<LifecycleResultDto> SetAgentHiddenAsync(string id, bool hidden, CancellationToken cancellationToken = default) => PostForAsync<LifecycleResultDto>($"/api/agents/{Id(id)}/hide", new SetHiddenDto(hidden), cancellationToken);

    public Task<LifecycleResultDto> ArchiveAgentAsync(string id, CancellationToken cancellationToken = default) => PostForAsync<LifecycleResultDto>($"/api/agents/{Id(id)}/archive", null, cancellationToken);

    public Task<LifecycleResultDto> DeleteAgentAsync(string id, CancellationToken cancellationToken = default) => SendForAsync<LifecycleResultDto>(HttpMethod.Delete, $"/api/agents/{Id(id)}", null, cancellationToken);

    // ---- Fleet thread, tasks, alerts, ledger ----------------------------------------------------------------------

    public Task<IReadOnlyList<ThreadEntryDto>> GetThreadAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<ThreadEntryDto>>("/api/thread", cancellationToken);

    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<TaskDto>>("/api/tasks", cancellationToken);

    public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<AlertDto>>("/api/alerts", cancellationToken);

    public Task<LedgerDto> GetLedgerAsync(CancellationToken cancellationToken = default) => GetAsync<LedgerDto>("/api/ledger", cancellationToken);

    // ---- Approvals ------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<ApprovalDto>>("/api/approvals", cancellationToken);

    /// <summary>Settles an approval; <paramref name="decision"/> is one of <see cref="ApprovalDecisions"/>.</summary>
    public Task DecideApprovalAsync(string id, string nonce, string decision, CancellationToken cancellationToken = default) => PostAsync($"/api/approvals/{Id(id)}", new ApprovalDecisionDto(nonce, decision), cancellationToken);

    /// <summary>The nonce-bound action used by push notification buttons (WP6 <c>NotificationFactory.ApprovalActionPath</c>).</summary>
    public Task ActOnApprovalAsync(string id, string nonce, string decision, CancellationToken cancellationToken = default) =>
        PostAsync($"/api/approvals/{Id(id)}/act?nonce={Uri.EscapeDataString(nonce)}&decision={Uri.EscapeDataString(decision)}", null, cancellationToken);

    // ---- Questions ------------------------------------------------------------------------------------------------

    public Task<QuestionDto> GetQuestionAsync(string id, CancellationToken cancellationToken = default) => GetAsync<QuestionDto>($"/api/questions/{Id(id)}", cancellationToken);

    public Task AnswerQuestionAsync(string id, string answer, CancellationToken cancellationToken = default) => PostAsync($"/api/questions/{Id(id)}", new QuestionAnswerDto(answer), cancellationToken);

    // ---- Proposals ------------------------------------------------------------------------------------------------

    /// <summary>Lists proposals; a null <paramref name="status"/> lists all (else Pending, Applied, Rejected, Stale or Invalid).</summary>
    public Task<IReadOnlyList<ProposalDto>> GetProposalsAsync(string? status = null, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ProposalDto>>(status is null ? "/api/proposals" : $"/api/proposals?status={Uri.EscapeDataString(status)}", cancellationToken);

    public Task<ProposalDto> GetProposalAsync(string id, CancellationToken cancellationToken = default) => GetAsync<ProposalDto>($"/api/proposals/{Id(id)}", cancellationToken);

    /// <returns>The proposal after the decision; its status can be Stale or Invalid even when approved.</returns>
    public Task<ProposalDto> DecideProposalAsync(string id, bool approve, CancellationToken cancellationToken = default) => PostForAsync<ProposalDto>($"/api/proposals/{Id(id)}", new ProposalDecisionDto(approve), cancellationToken);

    // ---- Memory ---------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<MemoryPageInfoDto>> GetMemoryPagesAsync(string? prefix = null, CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<MemoryPageInfoDto>>($"/api/memory/pages?prefix={Uri.EscapeDataString(prefix ?? string.Empty)}", cancellationToken);

    public Task<MemoryPageDto> GetMemoryPageAsync(string path, CancellationToken cancellationToken = default) => GetAsync<MemoryPageDto>($"/api/memory/page?path={Uri.EscapeDataString(path)}", cancellationToken);

    public Task SaveMemoryPageAsync(string path, string content, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, "/api/memory/page", new SaveMemoryPageDto(path, content), cancellationToken);

    public Task DeleteMemoryPageAsync(string path, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, $"/api/memory/page?path={Uri.EscapeDataString(path)}", null, cancellationToken);

    public Task<IReadOnlyList<PendingMemoryChangeDto>> GetPendingMemoryAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<PendingMemoryChangeDto>>("/api/memory/pending", cancellationToken);

    public Task DecidePendingMemoryAsync(string id, bool apply, CancellationToken cancellationToken = default) => PostAsync($"/api/memory/pending/{Id(id)}", new PendingChangeDecisionDto(apply), cancellationToken);

    /// <summary>Streams the wiki as a zip. The caller owns and must dispose the stream.</summary>
    public async Task<Stream> ExportMemoryAsync(CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(new Uri("/api/memory/export", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ---- Routines -------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<RoutineDto>> GetRoutinesAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<RoutineDto>>("/api/routines", cancellationToken);

    /// <summary>Enables a routine. The server answers 409 unless <paramref name="acknowledgedMonthlyCredits"/> equals its current projection (FR-RTN-09).</summary>
    public Task EnableRoutineAsync(string id, decimal acknowledgedMonthlyCredits, CancellationToken cancellationToken = default) => PostAsync($"/api/routines/{Id(id)}/enable", new EnableRoutineDto(acknowledgedMonthlyCredits), cancellationToken);

    public Task DisableRoutineAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/routines/{Id(id)}/disable", null, cancellationToken);

    public Task<DryRunResultDto> DryRunRoutineAsync(string id, CancellationToken cancellationToken = default) => PostForAsync<DryRunResultDto>($"/api/routines/{Id(id)}/dry-run", null, cancellationToken);

    /// <summary>Re-arms an auto-paused routine after the user confirms it is still wanted.</summary>
    public Task ConfirmRoutineResumeAsync(string id, CancellationToken cancellationToken = default) => PostAsync($"/api/routines/{Id(id)}/confirm-resume", null, cancellationToken);

    // ---- Budgets, audit, kill switch ------------------------------------------------------------------------------

    public Task<BudgetsDto> GetBudgetsAsync(CancellationToken cancellationToken = default) => GetAsync<BudgetsDto>("/api/budgets", cancellationToken);

    /// <summary>Newest-first audit entries.</summary>
    public Task<IReadOnlyList<AuditEntryDto>> GetAuditAsync(int limit = 100, CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<AuditEntryDto>>($"/api/audit?limit={limit}", cancellationToken);

    public Task<AuditVerifyDto> VerifyAuditAsync(CancellationToken cancellationToken = default) => GetAsync<AuditVerifyDto>("/api/audit/verify", cancellationToken);

    public Task<KillSwitchDto> GetKillSwitchAsync(CancellationToken cancellationToken = default) => GetAsync<KillSwitchDto>("/api/killswitch", cancellationToken);

    /// <summary>Allowed from any device.</summary>
    public Task EngageKillSwitchAsync(string reason, CancellationToken cancellationToken = default) => PostAsync("/api/killswitch", new KillSwitchRequestDto(reason), cancellationToken);

    /// <summary>Desktop only: a phone device receives 403, surfaced as <see cref="HttpRequestException"/>.</summary>
    public Task ReleaseKillSwitchAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, "/api/killswitch", null, cancellationToken);

    // ---- Connections ----------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<ConnectionDto>>("/api/connections", cancellationToken);

    /// <summary>Desktop only (FR-CON-07).</summary>
    public Task SetConnectionEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, $"/api/connections/{Id(id)}/enabled", new SetConnectionEnabledDto(enabled), cancellationToken);

    // ---- Push, presence, settings ---------------------------------------------------------------------------------

    public Task<VapidKeyDto> GetVapidKeyAsync(CancellationToken cancellationToken = default) => GetAsync<VapidKeyDto>("/api/push/vapid-key", cancellationToken);

    public Task AddPushSubscriptionAsync(PushSubscriptionDto subscription, CancellationToken cancellationToken = default) => PostAsync("/api/push/subscriptions", subscription, cancellationToken);

    public Task RemovePushSubscriptionAsync(string endpoint, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, $"/api/push/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}", null, cancellationToken);

    /// <summary>Tells the server whether the PWA has focus, so it suppresses push (FR-CLI-07).</summary>
    public Task SetPresenceAsync(bool focused, CancellationToken cancellationToken = default) => PostAsync("/api/presence", new PresenceDto(focused), cancellationToken);

    public Task<NotificationSettingsDto> GetNotificationSettingsAsync(CancellationToken cancellationToken = default) => GetAsync<NotificationSettingsDto>("/api/settings/notifications", cancellationToken);

    public Task SaveNotificationSettingsAsync(NotificationSettingsDto settings, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, "/api/settings/notifications", settings, cancellationToken);

    // ---- Plumbing -------------------------------------------------------------------------------------------------

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private Task PostAsync(string path, object? body, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, path, body, cancellationToken);

    private Task<T> PostForAsync<T>(string path, object? body, CancellationToken cancellationToken) => SendForAsync<T>(HttpMethod.Post, path, body, cancellationToken);

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(CreateRequest(method, path, body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<T> SendForAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(CreateRequest(method, path, body), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body) => new(method, new Uri(path, UriKind.Relative))
    {
        Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: JsonOptions),
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        var route = response.RequestMessage?.RequestUri?.AbsolutePath;

        // A 200 that is not JSON is the SPA fallback or a proxy page answering for a missing route: never parse it as data.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new HttpRequestException($"Unexpected non-JSON response ({mediaType ?? "no content type"}) for {route}.", null, response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new HttpRequestException($"The gateway returned an empty body for {route}.", null, response.StatusCode);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new HttpRequestException($"Unexpected non-JSON response for {route}: {ex.Message}", ex, response.StatusCode);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 500)
        {
            detail = detail[..500];
        }

        var route = response.RequestMessage?.RequestUri?.AbsolutePath;
        var reason = response.StatusCode == HttpStatusCode.NotFound ? "not found (route missing or item gone)" : response.ReasonPhrase;
        throw new HttpRequestException($"{(int)response.StatusCode} {reason} for {route}{(detail.Length > 0 ? $": {detail}" : string.Empty)}", null, response.StatusCode);
    }
}
