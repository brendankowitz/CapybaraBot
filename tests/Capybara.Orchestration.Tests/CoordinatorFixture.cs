using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
using Capybara.Policy.Provenance;
using Capybara.Policy.Safety;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Orchestration.Tests;

internal sealed class CoordinatorFixture : IDisposable
{
    private readonly TempDirectory _dir = new();

    public CoordinatorFixture(params AgentDefinition[] agents)
    {
        KillSwitch = new KillSwitch(Time);
        Registry = new AgentRegistry(agents, KillSwitch);
        Envelopes = new EnvelopeFactory(Time);
        Inbox = new FileInboxStore(Path.Combine(_dir.Path, "inbox"), Time);
        Tasks = new FileTaskStore(Path.Combine(_dir.Path, "tasks"));
        Coordinator = new A2ACoordinator(Registry, Envelopes, Inbox, Tasks, Thread, Taint, KillSwitch, Audit, Executor, new A2AOptions(), Time);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public KillSwitch KillSwitch { get; }

    public AgentRegistry Registry { get; }

    public EnvelopeFactory Envelopes { get; }

    public FileInboxStore Inbox { get; }

    public FileTaskStore Tasks { get; }

    public FleetThread Thread { get; } = new();

    public TaintTracker Taint { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public FakeExecutor Executor { get; } = new();

    public A2ACoordinator Coordinator { get; }

    public Envelope Root(string agent) =>
        Envelopes.CreateRoot("user", AgentId.Parse(agent), new Budget(10m, 100, TimeSpan.FromMinutes(10)), TimeSpan.FromMinutes(10));

    public void Dispose()
    {
        Coordinator.Dispose();
        KillSwitch.Dispose();
        _dir.Dispose();
    }
}

internal sealed class FakeExecutor : IAgentTurnExecutor
{
    public string Answer { get; set; } = "42";

    public Taint ReplyTaint { get; set; } = Taint.Clean;

    public List<(AgentId Target, Envelope Envelope)> Calls { get; } = [];

    public Task<ConsultReply> ConsultAsync(AgentDefinition target, string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
    {
        Calls.Add((target.Id, envelope));
        return Task.FromResult(new ConsultReply(Answer, ReplyTaint, []));
    }
}
