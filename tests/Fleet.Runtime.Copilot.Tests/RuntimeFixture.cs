using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;
using Fleet.Memory.Store;
using Fleet.Memory.Versioning;
using Fleet.Observability.Alerts;
using Fleet.Observability.Cost;
using Fleet.Orchestration.Coordination;
using Fleet.Orchestration.Envelopes;
using Fleet.Policy.Approvals;
using Fleet.Policy.Gate;
using Fleet.Policy.Provenance;
using Fleet.Policy.Rules;
using Fleet.Policy.Safety;
using Fleet.Runtime.Copilot.Configuration;
using Fleet.Runtime.Copilot.Hooks;
using Fleet.Runtime.Copilot.Sessions;
using Fleet.Runtime.Copilot.Tools;
using Fleet.Runtime.Copilot.Usage;
using Fleet.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Runtime.Copilot.Tests;

internal sealed class RuntimeFixture : IDisposable
{
    private readonly TempDirectory _dir = new();

    public RuntimeFixture(IEnumerable<PolicyRule>? rules = null)
    {
        Options = new CopilotRuntimeOptions { StateRoot = Path.Combine(_dir.Path, "state"), ConfigRoot = Path.Combine(_dir.Path, "config") };
        var rulesEngine = new RulesEngine(rules ?? []);
        KillSwitch = new KillSwitch(Time);
        Approvals = new ApprovalService(rulesEngine, new ApprovalOptions(), Time);
        Gate = new PolicyGate(rulesEngine, Taint, Approvals, KillSwitch, new InMemoryAuditLog());
        Ledger = new CostLedger(Alerts, Time);
        Handlers = new SessionHandlers(Gate, Taint, Allowances, new UnattendedUserInputBroker(), Options, NullLogger<SessionHandlers>.Instance);
        Usage = new UsageCapture(Ledger, Time, NullLogger<UsageCapture>.Instance);
        var memory = new WikiStore(new MemoryOptions { Root = Path.Combine(_dir.Path, "memory") }, new NoMemoryVersioning(), Time);
        Tools = new FleetToolProvider(new Lazy<A2ACoordinator>(() => throw new InvalidOperationException("not used")), memory, Taint);
        Configs = new SessionConfigFactory(Handlers, Usage, Tools, Options);
        Envelopes = new EnvelopeFactory(Time);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public CopilotRuntimeOptions Options { get; }

    public KillSwitch KillSwitch { get; }

    public TaintTracker Taint { get; } = new();

    public ApprovalService Approvals { get; }

    public PolicyGate Gate { get; }

    public InMemoryAlertSink Alerts { get; } = new();

    public CostLedger Ledger { get; }

    public PermissionAllowances Allowances { get; } = new();

    public SessionHandlers Handlers { get; }

    public UsageCapture Usage { get; }

    public FleetToolProvider Tools { get; }

    public SessionConfigFactory Configs { get; }

    public EnvelopeFactory Envelopes { get; }

    public FleetSession Session(AgentDefinition agent, SessionKind kind = SessionKind.Main, int depth = 0)
    {
        var envelope = Envelopes.CreateRoot("user", agent.Id, new Budget(5m, 50, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5)) with { Depth = depth };
        var id = kind == SessionKind.Consult ? SessionIds.Consult(agent.Id, Guid.NewGuid()) : SessionIds.Main(agent.Id);
        return new FleetSession(agent, id, kind, envelope);
    }

    public void Dispose()
    {
        KillSwitch.Dispose();
        _dir.Dispose();
    }
}
