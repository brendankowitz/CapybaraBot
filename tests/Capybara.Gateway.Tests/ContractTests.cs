using System.IO.Compression;
using System.Net;
using System.Reflection;
using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Proposals;
using Capybara.Memory.Access;
using Capybara.Memory.Store;
using Capybara.Orchestration.Proposals;
using Capybara.Policy.Approvals;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>
/// WP11 acceptance 1: every <see cref="CapybaraApiClient"/> method against the in-memory gateway. Each call reaches a
/// mapped route (never the /api 404 fallback or the SPA shell) and its DTO deserializes; ids refer to real items.
/// </summary>
public sealed class ContractTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_client_method_reaches_a_route_and_reads_its_dto()
    {
        using var factory = new ApiFactory();
        var recorder = new RecordingHandler();
        var api = factory.Api(factory.DesktopToken, recorder);
        var called = new HashSet<string>(StringComparer.Ordinal);
        void Called(string method) => called.Add(method);

        // Agents and chat.
        var agents = await api.GetAgentsAsync(Ct);
        Called(nameof(api.GetAgentsAsync));
        Assert.Contains(agents, a => a is { Id: "ea", State: "idle", Unread: 0, Hidden: false });
        await api.PauseAgentAsync("ea", Ct);
        Called(nameof(api.PauseAgentAsync));
        await api.ResumeAgentAsync("ea", Ct);
        Called(nameof(api.ResumeAgentAsync));
        var turn = await api.SendTurnAsync("ea", "hello", Ct);
        Called(nameof(api.SendTurnAsync));
        Assert.True(turn.IsAccepted);
        Assert.False(string.IsNullOrEmpty(turn.MessageId));
        await api.MarkAgentReadAsync("ea", Ct);
        Called(nameof(api.MarkAgentReadAsync));
        await api.SetModelAsync("ea", "gpt-5-mini", Ct);
        Called(nameof(api.SetModelAsync));
        var history = await api.GetChatHistoryAsync("ea", 100, Ct);
        Called(nameof(api.GetChatHistoryAsync));
        Assert.Contains(history, e => e is { Role: "user", Content: "hello" } && e.MessageId == turn.MessageId);
        var runtimes = await api.GetRuntimesAsync(Ct);
        Called(nameof(api.GetRuntimesAsync));
        Assert.Contains(runtimes, r => r.Id == "ea");
        await api.UnquarantineRuntimeAsync("ea", Ct);
        Called(nameof(api.UnquarantineRuntimeAsync));

        // Lifecycle (desktop).
        Assert.Equal(["assistant"], await api.GetAgentTemplatesAsync(Ct));
        Called(nameof(api.GetAgentTemplatesAsync));
        Assert.True((await api.CreateAgentAsync(new CreateAgentDto("assistant", "helper", "Helper"), Ct)).Applied);
        Called(nameof(api.CreateAgentAsync));
        Assert.True((await api.DuplicateAgentAsync("ea", new DuplicateAgentDto("ea2", "EA two"), Ct)).Applied);
        Called(nameof(api.DuplicateAgentAsync));
        Assert.True((await api.SetAgentHiddenAsync("ea2", true, Ct)).Applied);
        Called(nameof(api.SetAgentHiddenAsync));
        Assert.True((await api.ArchiveAgentAsync("ea2", Ct)).Applied);
        Called(nameof(api.ArchiveAgentAsync));
        Assert.True((await api.DeleteAgentAsync("helper", Ct)).Applied);
        Called(nameof(api.DeleteAgentAsync));

        // Thread, tasks, alerts, ledger.
        await api.GetThreadAsync(Ct);
        Called(nameof(api.GetThreadAsync));
        await api.GetTasksAsync(Ct);
        Called(nameof(api.GetTasksAsync));
        await api.GetAlertsAsync(Ct);
        Called(nameof(api.GetAlertsAsync));
        await api.GetLedgerAsync(Ct);
        Called(nameof(api.GetLedgerAsync));

        // Approvals: the list carries the full arguments; both settlement routes work.
        var approvals = factory.Services.GetRequiredService<ApprovalService>();
        var first = approvals.Request(Ea, SessionIds.Main(Ea), "email_send", "h1", "ea → email_send {…", RiskLevel.Medium, MessageOrigin.Agent, argsJson: """{"to":"sam@example.com"}""");
        var second = approvals.Request(Ea, SessionIds.Main(Ea), "email_send", "h2", "p", RiskLevel.High, MessageOrigin.Agent, argsJson: "{}");
        var listed = await api.GetApprovalsAsync(Ct);
        Called(nameof(api.GetApprovalsAsync));
        Assert.Contains(listed, a => a.Id == first.Id && a.ArgsJson == """{"to":"sam@example.com"}""" && a.Risk == "Medium" && a.Origin == "Agent");
        await api.DecideApprovalAsync(first.Id, first.Nonce, ApprovalDecisions.AllowOnce, Ct);
        Called(nameof(api.DecideApprovalAsync));
        await api.ActOnApprovalAsync(second.Id, second.Nonce, "deny", Ct);
        Called(nameof(api.ActOnApprovalAsync));

        // Questions.
        var escalation = factory.Services.GetRequiredService<PhoneUserEscalation>();
        var asked = escalation.AskAsync("q1", Ea, "Which room?", "Pick one.", TimeSpan.FromMinutes(5), Ct).AsTask();
        var question = await api.GetQuestionAsync("q1", Ct);
        Called(nameof(api.GetQuestionAsync));
        Assert.Equal(("ea", "Which room?"), (question.Agent, question.Title));
        await api.AnswerQuestionAsync("q1", "B", Ct);
        Called(nameof(api.AnswerQuestionAsync));
        Assert.Equal("B", await asked.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        // Proposals.
        var proposal = await factory.Services.GetRequiredService<ProposalService>().ProposeAsync(Ea, ProposalKind.Soul, null, "Be brief.\n", "shorter answers", Ct);
        Assert.Contains(await api.GetProposalsAsync(cancellationToken: Ct), p => p.Id == proposal.Id);
        Called(nameof(api.GetProposalsAsync));
        Assert.Equal("Pending", (await api.GetProposalAsync(proposal.Id, Ct)).Status);
        Called(nameof(api.GetProposalAsync));
        Assert.Equal("Rejected", (await api.DecideProposalAsync(proposal.Id, approve: false, Ct)).Status);
        Called(nameof(api.DecideProposalAsync));

        // Memory.
        await api.SaveMemoryPageAsync("/user/notes.md", "remember the milk\n", Ct);
        Called(nameof(api.SaveMemoryPageAsync));
        Assert.Contains(await api.GetMemoryPagesAsync("/user/", Ct), p => p.Path == "/user/notes.md" && p.Size > 0);
        Called(nameof(api.GetMemoryPagesAsync));
        Assert.Equal("remember the milk\n", (await api.GetMemoryPageAsync("/user/notes.md", Ct)).Content);
        Called(nameof(api.GetMemoryPageAsync));
        await using (var zip = await api.ExportMemoryAsync(Ct))
        {
            Called(nameof(api.ExportMemoryAsync));
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, e => e.FullName == "user/notes.md");
        }

        await api.DeleteMemoryPageAsync("/user/notes.md", Ct);
        Called(nameof(api.DeleteMemoryPageAsync));
        var store = factory.Services.GetRequiredService<WikiStore>();
        await store.RememberAsync(new MemoryAccessContext { Agent = Ea, SessionId = SessionIds.Main(Ea) }, "/user/preferences.md", "Prefers tea.", "preferences", Ct);
        var pending = Assert.Single(await api.GetPendingMemoryAsync(Ct));
        Called(nameof(api.GetPendingMemoryAsync));
        Assert.Equal(("/user/preferences.md", "Write"), (pending.Path, pending.Kind));
        await api.DecidePendingMemoryAsync(pending.Id, apply: false, Ct);
        Called(nameof(api.DecidePendingMemoryAsync));

        // Routines.
        var routines = await api.GetRoutinesAsync(Ct);
        Called(nameof(api.GetRoutinesAsync));
        var heartbeat = Assert.Single(routines, r => r.Id == "ea-heartbeat");
        Assert.True(heartbeat.ProjectedMonthlyCredits > 0);
        await api.EnableRoutineAsync("ea-heartbeat", heartbeat.ProjectedMonthlyCredits, Ct);
        Called(nameof(api.EnableRoutineAsync));
        var dryRun = await api.DryRunRoutineAsync("ea-heartbeat", Ct);
        Called(nameof(api.DryRunRoutineAsync));
        Assert.False(string.IsNullOrEmpty(dryRun.Status));
        await api.DisableRoutineAsync("ea-heartbeat", Ct);
        Called(nameof(api.DisableRoutineAsync));
        await api.ConfirmRoutineResumeAsync("ea-heartbeat", Ct);
        Called(nameof(api.ConfirmRoutineResumeAsync));

        // Budgets, audit, kill switch.
        var budgets = await api.GetBudgetsAsync(Ct);
        Called(nameof(api.GetBudgetsAsync));
        Assert.Contains(budgets.Agents, b => b.Agent == "ea");
        var audit = await api.GetAuditAsync(5, Ct);
        Called(nameof(api.GetAuditAsync));
        Assert.InRange(audit.Count, 1, 5);
        Assert.True(audit.Zip(audit.Skip(1)).All(p => p.First.Sequence > p.Second.Sequence), "newest first");
        Assert.True((await api.VerifyAuditAsync(Ct)).Valid);
        Called(nameof(api.VerifyAuditAsync));
        Assert.False((await api.GetKillSwitchAsync(Ct)).IsEngaged);
        Called(nameof(api.GetKillSwitchAsync));
        await api.EngageKillSwitchAsync("contract", Ct);
        Called(nameof(api.EngageKillSwitchAsync));
        await api.ReleaseKillSwitchAsync(Ct);
        Called(nameof(api.ReleaseKillSwitchAsync));

        // Connections.
        var connection = Assert.Single(await api.GetConnectionsAsync(Ct));
        Called(nameof(api.GetConnectionsAsync));
        Assert.Equal((ApiFactory.Connection, true, "ok"), (connection.Id, connection.Enabled, connection.Health?.Status));
        await api.SetConnectionEnabledAsync(ApiFactory.Connection, false, Ct);
        Called(nameof(api.SetConnectionEnabledAsync));

        // Push, presence, settings.
        Assert.Equal(ApiFactory.VapidKey, (await api.GetVapidKeyAsync(Ct)).PublicKey);
        Called(nameof(api.GetVapidKeyAsync));
        var subscription = PushTests.ValidSubscription();
        await api.AddPushSubscriptionAsync(subscription, Ct);
        Called(nameof(api.AddPushSubscriptionAsync));
        await api.RemovePushSubscriptionAsync(subscription.Endpoint, Ct);
        Called(nameof(api.RemovePushSubscriptionAsync));
        await api.SetPresenceAsync(focused: true, Ct);
        Called(nameof(api.SetPresenceAsync));
        var settings = await api.GetNotificationSettingsAsync(Ct);
        Called(nameof(api.GetNotificationSettingsAsync));
        await api.SaveNotificationSettingsAsync(settings, Ct);
        Called(nameof(api.SaveNotificationSettingsAsync));

        // Nothing reached the /api fallback or the SPA shell, and every client method was exercised.
        Assert.All(recorder.Exchanges, e =>
        {
            Assert.True(e.Status is >= 200 and < 300, $"{e.Method} {e.Path} answered {e.Status}: {e.Body}");
            Assert.NotEqual("text/html", e.MediaType);
        });
        var expected = typeof(CapybaraApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal) && m.Name != nameof(api.AwaitReplyAsync))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Empty(expected.Except(called));
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_problems_never_the_shell()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        foreach (var path in new[] { "/api/nope", "/api/agents/ea/nope", "/API/Nope", "/hubs/other" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        }

        var post = await client.PostAsync(new Uri("/api/nope", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);

        // The client reports it as a missing route rather than reading it as data.
        var missing = await Assert.ThrowsAsync<HttpRequestException>(() => new CapybaraApiClient(client).GetQuestionAsync("unknown", Ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
