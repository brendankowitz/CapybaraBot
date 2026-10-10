using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Policy.Approvals;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Policy.Redaction;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hooks;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Tools;
using Capybara.Runtime.Copilot.Usage;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Runtime.Copilot.Tests;

internal sealed class RuntimeFixture : IDisposable
{
    private readonly TempDirectory _dir = new();

    public RuntimeFixture(IEnumerable<PolicyRule>? rules = null, IUserInputBroker? userInput = null)
    {
        Options = new CopilotRuntimeOptions
        {
            StateRoot = Path.Combine(_dir.Path, "state"),
            ConfigRoot = Path.Combine(_dir.Path, "config"),
            BuiltInSkillDirectory = Path.Combine(_dir.Path, "builtin-skills"),
        };
        Rules = new RulesEngine(rules ?? []);
        KillSwitch = new KillSwitch(Time);
        Approvals = new ApprovalService(Rules, new ApprovalOptions(), Time);
        Gate = new PolicyGate(Rules, Taint, Approvals, KillSwitch, new InMemoryAuditLog());
        Ledger = new CostLedger(Alerts, Time);
        Handlers = new SessionHandlers(Gate, Taint, Allowances, userInput ?? new UnattendedUserInputBroker(), Options, NullLogger<SessionHandlers>.Instance, Redactor);
        Usage = new UsageCapture(Ledger, Time, NullLogger<UsageCapture>.Instance);
        var memory = new WikiStore(new MemoryOptions { Root = Path.Combine(_dir.Path, "memory") }, new NoMemoryVersioning(), Time);
        Tools = new CapybaraToolProvider(new Lazy<A2ACoordinator>(() => throw new InvalidOperationException("not used")), memory, Taint);
        Configs = new SessionConfigFactory(Handlers, Usage, Tools, Options, Rules, NullLogger<SessionConfigFactory>.Instance, Secrets);
        Envelopes = new EnvelopeFactory(Time);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public CopilotRuntimeOptions Options { get; }

    public string Root => _dir.Path;

    public RulesEngine Rules { get; }

    public SecretRedactor Redactor { get; } = new();

    public FakeSecretStore Secrets { get; } = new();

    public KillSwitch KillSwitch { get; }

    public TaintTracker Taint { get; } = new();

    public ApprovalService Approvals { get; }

    public PolicyGate Gate { get; }

    public InMemoryAlertSink Alerts { get; } = new();

    public CostLedger Ledger { get; }

    public PermissionAllowances Allowances { get; } = new();

    public SessionHandlers Handlers { get; }

    public UsageCapture Usage { get; }

    public CapybaraToolProvider Tools { get; }

    public SessionConfigFactory Configs { get; }

    public EnvelopeFactory Envelopes { get; }

    public CapybaraSession Session(AgentDefinition agent, SessionKind kind = SessionKind.Main, int depth = 0)
    {
        var envelope = Envelopes.CreateRoot("user", agent.Id, new Budget(5m, 50, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5)) with { Depth = depth };
        var id = kind == SessionKind.Consult ? SessionIds.Consult(agent.Id, Guid.NewGuid()) : SessionIds.Main(agent.Id);
        return new CapybaraSession(agent, id, kind, envelope);
    }

    public void Dispose()
    {
        KillSwitch.Dispose();
        _dir.Dispose();
    }
}
