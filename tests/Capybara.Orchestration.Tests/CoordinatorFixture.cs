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
    private SessionLimiter? _sessions;

    public CoordinatorFixture(params AgentDefinition[] agents)
    {
        KillSwitch = new KillSwitch(Time);
        Registry = new AgentRegistry(agents, KillSwitch);
        Envelopes = new EnvelopeFactory(Time);
        Inbox = new FileInboxStore(Path.Combine(_dir.Path, "inbox"), Time);
        Tasks = new FileTaskStore(Path.Combine(_dir.Path, "tasks"));
        Coordinator = NewCoordinator();
    }

    public A2AOptions Options { get; } = new();

    public SessionLimiter Sessions => _sessions ??= new SessionLimiter(Options);

    /// <summary>A coordinator over the same stores, as after a restart.</summary>
    public A2ACoordinator NewCoordinator() => new(Registry, Envelopes, Inbox, Tasks, Thread, Taint, KillSwitch, Audit, Executor, Options, Time, Sessions, new ExchangeLedger(Time, Options.ExchangeRetention, ExchangesPath));

    public string ExchangesPath => Path.Combine(_dir.Path, "a2a", "exchanges.json");

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
        _sessions?.Dispose();
        KillSwitch.Dispose();
        _dir.Dispose();
    }
}

internal sealed class FakeExecutor : IAgentTurnExecutor
{
    public string Answer { get; set; } = "42";

    public Taint ReplyTaint { get; set; } = Taint.Clean;

    public List<(AgentId Target, Envelope Envelope)> Calls { get; } = [];

    /// <summary>When set, consults throw it.</summary>
    public Exception? Failure { get; set; }

    public Task<ConsultReply> ConsultAsync(AgentDefinition target, string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
    {
        Calls.Add((target.Id, envelope));
        return Failure is { } failure ? Task.FromException<ConsultReply>(failure) : Task.FromResult(new ConsultReply(Answer, ReplyTaint, []));
    }
}
