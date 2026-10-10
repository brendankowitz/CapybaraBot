using System.Collections.Concurrent;
using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;
using Capybara.Observability.Alerts;
using Capybara.Orchestration.Thread;
using Capybara.Policy.Approvals;
using Capybara.Policy.Safety;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>
/// WP11 acceptance 1 (SignalR): every hub event the client subscribes to arrives through
/// <see cref="CapybaraRealtimeClient"/>'s typed handlers, with string enums and DTO-shaped payloads.
/// </summary>
public sealed class RealtimeContractTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_hub_event_deserializes_with_the_clients_typed_handlers()
    {
        using var factory = new RealtimeFactory();
        var server = factory.Server;
        await using var client = new CapybaraRealtimeClient(server.BaseAddress, () => Task.FromResult<string?>(factory.PhoneToken), options =>
        {
            options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.Headers[TailnetAuthenticationMiddleware.TailscaleLoginHeader] = GatewayFactory.Login;
        });
        var received = new ConcurrentQueue<(string Event, object Payload)>();
        using var status = client.OnAgentStatusChanged((agent, state, unread) => received.Enqueue(("AgentStatusChanged", (agent, state, unread))));
        using var thread = client.OnThreadEntry(e => received.Enqueue(("ThreadEntry", e)));
        using var requested = client.OnApprovalRequested(a => received.Enqueue(("ApprovalRequested", a)));
        using var resolved = client.OnApprovalResolved(id => received.Enqueue(("ApprovalResolved", id)));
        using var question = client.OnQuestionRequested(q => received.Enqueue(("QuestionRequested", q)));
        using var alert = client.OnAlert(a => received.Enqueue(("Alert", a)));
        using var kill = client.OnKillSwitchChanged(engaged => received.Enqueue(("KillSwitchChanged", engaged)));
        await client.StartAsync(Ct);

        var approvals = factory.Services.GetRequiredService<ApprovalService>();
        var approval = approvals.Request(Ea, SessionIds.Main(Ea), "email_send", "h", "ea → email_send {}", RiskLevel.High, MessageOrigin.Agent, argsJson: """{"to":"sam@example.com"}""");
        approvals.Decide(approval.Id, approval.Nonce, ApprovalDecision.Deny, MessageOrigin.User);
        factory.Services.GetRequiredService<FleetThread>().Append(new FleetThreadEntry(factory.Time.GetUtcNow(), Ea, AgentId.Parse("research"), A2AOperation.Consult, "asked about the agenda", "trace-1", 1, Taint.Tainted));
        await factory.Services.GetRequiredService<IAlertSink>().PublishAsync(new Alert("test.alert", AlertSeverity.Warning, "something to see", factory.Time.GetUtcNow(), "ea"), Ct);
        var asked = factory.Services.GetRequiredService<PhoneUserEscalation>().AskAsync("q-rt", Ea, "Room?", "A or B", TimeSpan.FromMinutes(1), Ct).AsTask();
        await factory.Services.GetRequiredService<AgentActivityTracker>().TurnStartedAsync("ea", Ct);
        factory.Services.GetRequiredService<KillSwitch>().Engage("drill");

        await Eventually.TrueAsync(() => received.Select(r => r.Event).Distinct().Count() == 7, "every event type arrives");

        var dto = received.Select(r => r.Payload).OfType<ApprovalDto>().Single();
        Assert.Equal((approval.Id, "High", "Agent", """{"to":"sam@example.com"}"""), (dto.Id, dto.Risk, dto.Origin, dto.ArgsJson));
        Assert.Contains(received, r => r is { Event: "ApprovalResolved", Payload: string id } && id == approval.Id);
        var entry = received.Select(r => r.Payload).OfType<ThreadEntryDto>().Single();
        Assert.Equal(("ea", "research", "Consult", "Tainted"), (entry.From, entry.To, entry.Operation, entry.Taint));
        Assert.Contains(received.Select(r => r.Payload).OfType<AlertDto>(), a => a is { Kind: "test.alert", Severity: "Warning", Agent: "ea" });
        Assert.Equal(("q-rt", "ea", "Room?"), received.Select(r => r.Payload).OfType<QuestionDto>().Select(q => (q.Id, q.Agent, q.Title)).Single());
        // The pending approval outranks the running turn until it is read.
        Assert.Contains(received, r => r.Event == "AgentStatusChanged" && r.Payload.Equals(("ea", "needsAttention", 1)));
        Assert.Contains(received, r => r is { Event: "KillSwitchChanged", Payload: true });

        factory.Services.GetRequiredService<PhoneUserEscalation>().SubmitAnswer("q-rt", "A");
        Assert.Equal("A", await asked.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        factory.Services.GetRequiredService<KillSwitch>().Release(ControlSurface.Desktop);
    }
}
