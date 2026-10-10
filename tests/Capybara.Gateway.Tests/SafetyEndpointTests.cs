using System.Net;
using System.Net.Http.Json;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Gateway.Tests;

/// <summary>WP11 acceptance 4 and 5: approval settlement (both routes) and routine control.</summary>
public sealed class SafetyEndpointTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_push_action_route_needs_the_nonce_and_a_device_and_is_single_use()
    {
        using var factory = new ApiFactory();
        var approvals = factory.Services.GetRequiredService<ApprovalService>();
        var request = approvals.Request(Ea, SessionIds.Main(Ea), "email_send", "h", "p", RiskLevel.High, MessageOrigin.Agent);
        var phone = factory.CreateDeviceClient(factory.PhoneToken);
        Uri Act(string nonce, string decision) => new($"/api/approvals/{request.Id}/act?nonce={nonce}&decision={decision}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateDeviceClient(token: null).PostAsync(Act(request.Nonce, "allow-once"), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync(Act(new string('0', 32), "allow-once"), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync(new Uri($"/api/approvals/{request.Id}/act?decision=allow-once", UriKind.Relative), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync(Act(request.Nonce, "maybe"), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsync(Act(request.Nonce, "1"), null, Ct)).StatusCode);
        Assert.Equal(ApprovalStatus.Pending, approvals.Get(request.Id)!.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync(Act(request.Nonce, "allow-once"), null, Ct)).StatusCode);
        Assert.Equal(ApprovalStatus.Approved, approvals.Get(request.Id)!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await phone.PostAsync(Act(request.Nonce, "deny"), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.PostAsync(new Uri($"/api/approvals/unknown/act?nonce={request.Nonce}&decision=deny", UriKind.Relative), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Always_allow_is_refused_for_high_risk_approvals()
    {
        using var factory = new ApiFactory();
        var request = factory.Services.GetRequiredService<ApprovalService>().Request(Ea, SessionIds.Main(Ea), "email_send", "h", "p", RiskLevel.High, MessageOrigin.Agent);

        var response = await factory.CreateDeviceClient(factory.DesktopToken).PostAsJsonAsync(new Uri($"/api/approvals/{request.Id}", UriKind.Relative), new ApprovalDecisionDto(request.Nonce, ApprovalDecisions.AlwaysAllow), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_decision_whose_audit_record_cannot_be_written_is_a_server_error_and_is_not_applied()
    {
        using var factory = new FailingAuditFactory();
        var approvals = factory.Services.GetRequiredService<ApprovalService>();
        var request = approvals.Request(Ea, SessionIds.Main(Ea), "email_send", "h", "p", RiskLevel.Medium, MessageOrigin.Agent);

        var response = await factory.CreateDeviceClient(factory.PhoneToken).PostAsJsonAsync(new Uri($"/api/approvals/{request.Id}", UriKind.Relative), new ApprovalDecisionDto(request.Nonce, ApprovalDecisions.AllowOnce), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, approvals.Get(request.Id)!.Status);
        Assert.False(approvals.TryConsume(Ea, SessionIds.Main(Ea), "email_send", "h"));
    }

    [Fact]
    public async Task Enabling_a_routine_needs_the_current_projection_acknowledged()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateDeviceClient(factory.PhoneToken);
        var registry = factory.Services.GetRequiredService<RoutineRegistry>();
        var projected = registry.ProjectedMonthlyCredits(registry.Find("ea-heartbeat")!);

        var stale = await client.PostAsJsonAsync(new Uri("/api/routines/ea-heartbeat/enable", UriKind.Relative), new EnableRoutineDto(projected + 1), Ct);
        var unknown = await client.PostAsJsonAsync(new Uri("/api/routines/nope/enable", UriKind.Relative), new EnableRoutineDto(projected), Ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(projected.ToString(System.Globalization.CultureInfo.InvariantCulture), await stale.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await new CapybaraApiClient(client).EnableRoutineAsync("ea-heartbeat", projected, Ct);
        Assert.True(registry.GetState("ea-heartbeat").Runnable);
    }

    [Fact]
    public async Task While_the_kill_switch_is_engaged_only_the_desktop_can_rearm_a_routine()
    {
        using var factory = new ApiFactory();
        var registry = factory.Services.GetRequiredService<RoutineRegistry>();
        var projected = registry.ProjectedMonthlyCredits(registry.Find("ea-heartbeat")!);
        registry.AutoPause("ea-heartbeat", "test");
        var phone = factory.CreateDeviceClient(factory.PhoneToken);
        factory.Services.GetRequiredService<KillSwitch>().Engage("drill");

        var resume = await phone.PostAsync(new Uri("/api/routines/ea-heartbeat/confirm-resume", UriKind.Relative), null, Ct);
        var enable = await phone.PostAsJsonAsync(new Uri("/api/routines/ea-heartbeat/enable", UriKind.Relative), new EnableRoutineDto(projected), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, resume.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, enable.StatusCode);
        Assert.True(registry.GetState("ea-heartbeat").AutoPaused);

        await factory.Api(factory.DesktopToken).ConfirmRoutineResumeAsync("ea-heartbeat", Ct);
        Assert.False(registry.GetState("ea-heartbeat").AutoPaused);

        // Merely auto-paused (no kill switch): any device may confirm.
        factory.Services.GetRequiredService<KillSwitch>().Release(ControlSurface.Desktop);
        registry.AutoPause("ea-heartbeat", "test");
        await new CapybaraApiClient(phone).ConfirmRoutineResumeAsync("ea-heartbeat", Ct);
        Assert.False(registry.GetState("ea-heartbeat").AutoPaused);
    }

    [Fact]
    public async Task Routines_list_their_newest_runs_first_capped_at_twenty()
    {
        using var factory = new ApiFactory();
        var history = factory.Services.GetRequiredService<Scheduling.History.RoutineRunHistory>();
        var start = factory.Time.GetUtcNow();
        for (var i = 0; i < 25; i++)
        {
            await history.RecordAsync(new Scheduling.History.RoutineRun("ea-heartbeat", start.AddMinutes(i), Scheduling.History.RoutineRunOutcome.Succeeded, CreditsSpent: i), Ct);
        }

        var routine = Assert.Single(await factory.Api(factory.PhoneToken).GetRoutinesAsync(Ct), r => r.Id == "ea-heartbeat");

        Assert.Equal(20, routine.LastRuns.Count);
        Assert.Equal(start.AddMinutes(24), routine.LastRuns[0].At);
        Assert.Equal("Succeeded", routine.LastRuns[0].Status);
    }

    private sealed class FailingAuditFactory : ApiFactory
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog, ApprovalAuditFails>();
        }
    }

    /// <summary>Accepts every record except approval decisions.</summary>
    private sealed class ApprovalAuditFails : IAuditLog
    {
        public ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
            record.Kind == "approval" && record.Decision is not "expired"
                ? throw new IOException("disk full")
                : ValueTask.FromResult(new AuditEntry { Sequence = 1, At = DateTimeOffset.UnixEpoch, Kind = record.Kind, Decision = record.Decision, PreviousHash = string.Empty });

        public async IAsyncEnumerable<AuditEntry> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
