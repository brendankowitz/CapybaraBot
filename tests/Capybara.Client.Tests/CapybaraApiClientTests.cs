using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Capybara.Client.Tests;

public sealed class CapybaraApiClientTests
{
    private static readonly Uri BaseAddress = new("https://capybara.example");

    private static (CapybaraApiClient Client, CaptureHandler Handler) Create(string body = "{}", HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        var handler = new CaptureHandler(body, status, contentType);
        return (new CapybaraApiClient(new HttpClient(handler) { BaseAddress = BaseAddress }), handler);
    }

    public static TheoryData<string, string, string, Func<CapybaraApiClient, Task>> CommandRoutes => new()
    {
        { "pause", "POST", "/api/agents/ea/pause", c => c.PauseAgentAsync("ea") },
        { "resume", "POST", "/api/agents/ea/resume", c => c.ResumeAgentAsync("ea") },
        { "model", "PUT", "/api/agents/ea/model", c => c.SetModelAsync("ea", "gpt-5") },
        { "unquarantine", "POST", "/api/runtimes/ea/unquarantine", c => c.UnquarantineRuntimeAsync("ea") },
        { "approval decide", "POST", "/api/approvals/a%2F1", c => c.DecideApprovalAsync("a/1", "n", ApprovalDecisions.AllowOnce) },
        { "approval act", "POST", "/api/approvals/a1/act?nonce=n%2B1&decision=Deny", c => c.ActOnApprovalAsync("a1", "n+1", ApprovalDecisions.Deny) },
        { "question answer", "POST", "/api/questions/q1", c => c.AnswerQuestionAsync("q1", "yes") },
        { "memory save", "PUT", "/api/memory/page", c => c.SaveMemoryPageAsync("/a.md", "x") },
        { "memory delete", "DELETE", "/api/memory/page?path=%2Fa.md", c => c.DeleteMemoryPageAsync("/a.md") },
        { "memory pending decide", "POST", "/api/memory/pending/p1", c => c.DecidePendingMemoryAsync("p1", apply: true) },
        { "routine enable", "POST", "/api/routines/r1/enable", c => c.EnableRoutineAsync("r1", 12.5m) },
        { "routine disable", "POST", "/api/routines/r1/disable", c => c.DisableRoutineAsync("r1") },
        { "routine confirm", "POST", "/api/routines/r1/confirm-resume", c => c.ConfirmRoutineResumeAsync("r1") },
        { "kill engage", "POST", "/api/killswitch", c => c.EngageKillSwitchAsync("why") },
        { "kill release", "DELETE", "/api/killswitch", c => c.ReleaseKillSwitchAsync() },
        { "connection enabled", "PUT", "/api/connections/email/enabled", c => c.SetConnectionEnabledAsync("email", enabled: false) },
        { "push subscribe", "POST", "/api/push/subscriptions", c => c.AddPushSubscriptionAsync(new PushSubscriptionDto("https://push/x", new PushKeysDto("p", "a"))) },
        { "push unsubscribe", "DELETE", "/api/push/subscriptions?endpoint=https%3A%2F%2Fpush%2Fx", c => c.RemovePushSubscriptionAsync("https://push/x") },
        { "presence", "POST", "/api/presence", c => c.SetPresenceAsync(focused: true) },
        { "notification settings save", "PUT", "/api/settings/notifications", c => c.SaveNotificationSettingsAsync(new NotificationSettingsDto([new AgentNotificationToggleDto("ea", false)], new QuietHoursDto(true, "22:00", "07:00"))) },
    };

    [Theory]
    [MemberData(nameof(CommandRoutes))]
    public async Task Commands_use_the_contract_method_and_path(string name, string method, string pathAndQuery, Func<CapybaraApiClient, Task> call)
    {
        var (client, handler) = Create(string.Empty, HttpStatusCode.NoContent);

        await call(client);

        Assert.Equal(method, handler.Method?.Method);
        Assert.Equal(pathAndQuery, handler.RequestUri?.PathAndQuery);
        Assert.NotEmpty(name);
    }

    public static TheoryData<string, string, string, Func<CapybaraApiClient, Task>> ReadRoutes => new()
    {
        { "agents", "/api/agents", """[{"id":"ea","name":"EA","job":"assist","model":"auto","paused":false,"budgetExhausted":true}]""", async c => Assert.True((await c.GetAgentsAsync())[0].BudgetExhausted) },
        { "history", "/api/agents/ea/history?limit=25", """[{"at":"2026-01-01T00:00:00Z","role":"user","content":"hi"}]""", async c => Assert.Equal("user", (await c.GetChatHistoryAsync("ea", 25))[0].Role) },
        { "runtimes", "/api/runtimes", """[{"id":"ea","state":"Running"}]""", async c => Assert.Equal("Running", (await c.GetRuntimesAsync())[0].State) },
        { "templates", "/api/lifecycle/templates", """["assistant"]""", async c => Assert.Equal("assistant", (await c.GetAgentTemplatesAsync())[0]) },
        { "thread", "/api/thread", """[{"at":"2026-01-01T00:00:00Z","from":"ea","to":"dev","operation":"Delegate","summary":"s","traceId":"t","depth":1,"taint":"Clean","rejected":false}]""", async c => Assert.Equal("dev", (await c.GetThreadAsync())[0].To) },
        { "tasks", "/api/tasks", """[{"id":"t1","agent":"ea","summary":"s","status":"working"}]""", async c => Assert.Equal("working", (await c.GetTasksAsync())[0].Status) },
        { "alerts", "/api/alerts", """[{"kind":"quota","severity":"Warning","message":"m","at":"2026-01-01T00:00:00Z"}]""", async c => Assert.Equal("Warning", (await c.GetAlertsAsync())[0].Severity) },
        { "ledger", "/api/ledger", """{"agents":[],"byModel":{},"byRoutine":{}}""", async c => Assert.Equal(JsonValueKind.Array, (await c.GetLedgerAsync()).Agents.ValueKind) },
        { "approvals", "/api/approvals", """[{"id":"a1","nonce":"n","agent":"ea","tool":"email_send","argsHash":"h","preview":"p","risk":"High","offersAlwaysAllow":false,"createdAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:10:00Z","reviewerVerdict":"escalate"}]""", async c => Assert.Equal("escalate", (await c.GetApprovalsAsync())[0].ReviewerVerdict) },
        { "question", "/api/questions/q%2F1", """{"id":"q/1","agent":"ea","title":"t","body":"b","choices":["a","b"]}""", async c => Assert.Equal(2, (await c.GetQuestionAsync("q/1")).Choices?.Count) },
        { "proposals", "/api/proposals?status=Pending", """[{"id":"p1","agent":"ea","kind":"Soul","name":null,"targetPath":"agents/ea/SOUL.md","diff":"-a\n+b","rationale":"r","status":"Pending","createdAt":"2026-01-01T00:00:00Z"}]""", async c => Assert.Equal("-a\n+b", (await c.GetProposalsAsync("Pending"))[0].Diff) },
        { "proposal", "/api/proposals/p1", """{"id":"p1","agent":"ea","kind":"Soul","targetPath":"x","diff":"d","rationale":"r","status":"Stale","createdAt":"2026-01-01T00:00:00Z"}""", async c => Assert.Equal("Stale", (await c.GetProposalAsync("p1")).Status) },
        { "memory pages", "/api/memory/pages?prefix=%2Fagents", """[{"path":"/agents/ea/a.md","size":12}]""", async c => Assert.Equal(12, (await c.GetMemoryPagesAsync("/agents"))[0].Size) },
        { "memory page", "/api/memory/page?path=%2Fa.md", """{"path":"/a.md","content":"hello"}""", async c => Assert.Equal("hello", (await c.GetMemoryPageAsync("/a.md")).Content) },
        { "memory pending", "/api/memory/pending", """[{"id":"p1","path":"/a.md","proposedBy":"ea","before":null,"after":"x","at":"2026-01-01T00:00:00Z","kind":"Delete","reason":"dup"}]""", async c => Assert.Equal("Delete", (await c.GetPendingMemoryAsync())[0].Kind) },
        { "routines", "/api/routines", """[{"id":"r1","owner":"ea","kind":"cron","enabled":false,"paused":false,"autoPaused":true,"projectedMonthlyCredits":42.5,"lastRuns":[{"at":"2026-01-01T00:00:00Z","status":"Completed","credits":1.5,"model":"auto"}]}]""", async c => Assert.Equal(1.5m, (await c.GetRoutinesAsync())[0].LastRuns[0].Credits) },
        { "budgets", "/api/budgets", """{"agents":[{"agent":"ea","spentToday":1,"dailyLimit":10,"spentThisMonth":5,"monthlyLimit":100,"exhausted":false}],"daily":{"spent":1,"limit":10},"monthly":{"spent":5,"limit":100},"byModel":[{"key":"auto","credits":5}],"byRoutine":[],"quota":{"used":50,"limit":300,"resetsAt":"2026-02-01T00:00:00Z"}}""", async c => Assert.Equal(300m, (await c.GetBudgetsAsync()).Quota?.Limit) },
        { "audit", "/api/audit?limit=10", """[{"sequence":3,"at":"2026-01-01T00:00:00Z","kind":"approval","decision":"allow","agent":"ea","tool":"email_send"}]""", async c => Assert.Equal(3, (await c.GetAuditAsync(10))[0].Sequence) },
        { "audit verify", "/api/audit/verify", """{"valid":true}""", async c => Assert.True((await c.VerifyAuditAsync()).Valid) },
        { "kill status", "/api/killswitch", """{"isEngaged":true,"reason":"r","engagedAt":"2026-01-01T00:00:00Z"}""", async c => Assert.True((await c.GetKillSwitchAsync()).IsEngaged) },
        { "connections", "/api/connections", """[{"id":"email","kind":"google","displayName":"Mail","enabled":true,"health":{"status":"expiring","detail":"d"}}]""", async c => Assert.Equal("expiring", (await c.GetConnectionsAsync())[0].Health?.Status) },
        { "vapid", "/api/push/vapid-key", """{"publicKey":"BKey"}""", async c => Assert.Equal("BKey", (await c.GetVapidKeyAsync()).PublicKey) },
        { "notification settings", "/api/settings/notifications", """{"agents":[{"agent":"ea","enabled":false}],"quietHours":{"enabled":true,"start":"22:00","end":"07:00"}}""", async c => Assert.False((await c.GetNotificationSettingsAsync()).Agents[0].Enabled) },
    };

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task Reads_use_the_contract_path_and_map_the_dto(string name, string pathAndQuery, string body, Func<CapybaraApiClient, Task> assert)
    {
        var (client, handler) = Create(body);

        await assert(client);

        Assert.Equal("GET", handler.Method?.Method);
        Assert.Equal(pathAndQuery, handler.RequestUri?.PathAndQuery);
        Assert.NotEmpty(name);
    }

    public static TheoryData<string, string, string, string, Func<CapybaraApiClient, Task>> ResultCommands => new()
    {
        { "turn", "POST", "/api/agents/ea/turn", """{"content":"done","sessionId":"s1"}""", async c => Assert.Equal("s1", (await c.SendTurnAsync("ea", "hi")).SessionId) },
        { "create", "POST", "/api/agents", """{"applied":true,"errors":[]}""", async c => Assert.True((await c.CreateAgentAsync(new CreateAgentDto("assistant", "ea2", "EA 2"))).Applied) },
        { "duplicate", "POST", "/api/agents/ea/duplicate", """{"applied":false,"errors":["exists"]}""", async c => Assert.Equal(["exists"], (await c.DuplicateAgentAsync("ea", new DuplicateAgentDto("ea2", "EA 2"))).Errors) },
        { "hide", "POST", "/api/agents/ea/hide", """{"applied":true,"errors":[]}""", async c => Assert.True((await c.SetAgentHiddenAsync("ea", hidden: true)).Applied) },
        { "archive", "POST", "/api/agents/ea/archive", """{"applied":true,"errors":[]}""", async c => Assert.True((await c.ArchiveAgentAsync("ea")).Applied) },
        { "delete", "DELETE", "/api/agents/ea", """{"applied":true,"errors":[]}""", async c => Assert.True((await c.DeleteAgentAsync("ea")).Applied) },
        { "dry run", "POST", "/api/routines/r1/dry-run", """{"status":"Completed","output":"o","deniedApprovals":["email_send"],"credits":0.5}""", async c => Assert.Equal(["email_send"], (await c.DryRunRoutineAsync("r1")).DeniedApprovals) },
        { "proposal decide", "POST", "/api/proposals/p1", """{"id":"p1","agent":"ea","kind":"Soul","targetPath":"x","diff":"d","rationale":"r","status":"Applied","createdAt":"2026-01-01T00:00:00Z"}""", async c => Assert.Equal("Applied", (await c.DecideProposalAsync("p1", approve: true)).Status) },
    };

    [Theory]
    [MemberData(nameof(ResultCommands))]
    public async Task Commands_with_results_use_the_contract_path_and_map_the_dto(string name, string method, string path, string body, Func<CapybaraApiClient, Task> assert)
    {
        var (client, handler) = Create(body);

        await assert(client);

        Assert.Equal(method, handler.Method?.Method);
        Assert.Equal(path, handler.RequestUri?.PathAndQuery);
        Assert.NotEmpty(name);
    }

    [Fact]
    public async Task Turn_body_carries_the_incognito_flag()
    {
        var (client, handler) = Create("""{"content":"ok"}""");

        await client.SendTurnAsync("ea", new TurnRequestDto("secret", Incognito: true, ConversationId: "c1"), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("incognito").GetBoolean());
        Assert.Equal("c1", body.RootElement.GetProperty("conversationId").GetString());
        Assert.Equal("secret", body.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Routine_enable_sends_the_acknowledged_projection()
    {
        var (client, handler) = Create(string.Empty, HttpStatusCode.NoContent);

        await client.EnableRoutineAsync("r1", 42.5m, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(42.5m, body.RootElement.GetProperty("acknowledgedMonthlyCredits").GetDecimal());
    }

    [Fact]
    public async Task Approval_decision_body_carries_nonce_and_decision()
    {
        var (client, handler) = Create(string.Empty, HttpStatusCode.NoContent);

        await client.DecideApprovalAsync("a1", "nonce-1", ApprovalDecisions.AlwaysAllow, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("nonce-1", body.RootElement.GetProperty("nonce").GetString());
        Assert.Equal("AlwaysAllow", body.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Push_subscription_body_matches_the_browser_shape()
    {
        var (client, handler) = Create(string.Empty, HttpStatusCode.NoContent);

        await client.AddPushSubscriptionAsync(new PushSubscriptionDto("https://push/x", new PushKeysDto("p256", "auth")), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("p256", body.RootElement.GetProperty("keys").GetProperty("p256dh").GetString());
        Assert.Equal("auth", body.RootElement.GetProperty("keys").GetProperty("auth").GetString());
    }

    [Fact]
    public async Task Memory_export_streams_the_zip_body()
    {
        var (client, handler) = Create("PK-zip-bytes", contentType: "application/zip");

        await using var stream = await client.ExportMemoryAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("/api/memory/export", handler.RequestUri?.PathAndQuery);
        Assert.Equal("PK-zip-bytes", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_routes_surface_an_error_instead_of_an_empty_result()
    {
        var (client, _) = Create(string.Empty, HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRoutinesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Missing_items_on_every_read_shape_surface_not_found()
    {
        var (client, _) = Create(string.Empty, HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetQuestionAsync("q1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMemoryPageAsync("/a.md", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteMemoryPageAsync("/a.md", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ExportMemoryAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DisableRoutineAsync("r1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.AnswerQuestionAsync("q1", "a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Conflict_surfaces_status_and_server_message()
    {
        var (client, _) = Create("projection is now 50", HttpStatusCode.Conflict, "text/plain");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.EnableRoutineAsync("r1", 42m, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Contains("projection is now 50", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Desktop_only_release_surfaces_forbidden_for_phones()
    {
        var (client, _) = Create(string.Empty, HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReleaseKillSwitchAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Empty_success_body_for_a_dto_route_is_an_error()
    {
        var (client, _) = Create("null");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBudgetsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Authentication_handler_adds_bearer_and_optional_login()
    {
        var capture = new CaptureHandler("{}");
        using var handler = new CapybaraAuthenticationHandler(new CapybaraClientOptions { DeviceToken = "device-token", TailscaleLoginForLocalTesting = "owner@example.com" }) { InnerHandler = capture };
        using var http = new HttpClient(handler) { BaseAddress = BaseAddress };

        await http.GetAsync(new Uri("/api/agents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(new AuthenticationHeaderValue("Bearer", "device-token"), capture.Authorization);
        Assert.Equal("owner@example.com", capture.Login);
    }

    private sealed class CaptureHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK, string contentType = "application/json") : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? Body { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? Login { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization;
            Login = request.Headers.TryGetValues(CapybaraAuthenticationHandler.TailscaleLoginHeader, out var values) ? values.Single() : null;
            return new HttpResponseMessage(statusCode) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType), RequestMessage = request };
        }
    }
}
