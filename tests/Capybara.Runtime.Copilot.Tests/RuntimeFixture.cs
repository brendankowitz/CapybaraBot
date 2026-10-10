using Capybara.Connections.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Store;
using Capybara.Memory.Versioning;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Envelopes;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Proposals;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Tasks;
using Capybara.Orchestration.Thread;
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

    public RuntimeFixture(
        IEnumerable<PolicyRule>? rules = null,
        IUserInputBroker? userInput = null,
        IBrokerToolSource? broker = null,
        IEnumerable<AgentDefinition>? fleet = null,
        IMemoryStore? memory = null,
        ProposalService? proposals = null)
    {
        Options = new CopilotRuntimeOptions
        {
            StateRoot = Path.Combine(_dir.Path, "state"),
            ConfigRoot = Path.Combine(_dir.Path, "config"),
            BuiltInSkillDirectory = Path.Combine(_dir.Path, "builtin-skills"),

            // Explicit, so Windows test runs exercise the Unix launch path against a stand-in launcher file.
            StdioMcpIsolation = StdioMcpIsolation.ClearEnvironment,
            EnvExecutable = Path.Combine(_dir.Path, "env"),
        };
        File.WriteAllText(Options.EnvExecutable, "stand-in env launcher");
        Rules = new RulesEngine(rules ?? []);
        KillSwitch = new KillSwitch(Time);
        Approvals = new ApprovalService(Rules, new ApprovalOptions(), Time);
        Gate = new PolicyGate(Rules, Taint, Approvals, KillSwitch, Audit);
        Ledger = new CostLedger(Alerts, Time);
        Handlers = new SessionHandlers(Gate, Taint, Allowances, userInput ?? new UnattendedUserInputBroker(), Options, NullLogger<SessionHandlers>.Instance, Alerts, Time, Redactor);
        Usage = new UsageCapture(Ledger, Time, NullLogger<UsageCapture>.Instance);
        Wiki = new WikiStore(new MemoryOptions { Root = Path.Combine(_dir.Path, "memory") }, new NoMemoryVersioning(), Time);
        Memory = new CompositeMemoryStore(Wiki, [], Time);
        Envelopes = new EnvelopeFactory(Time);
        Registry = new AgentRegistry(fleet ?? [], KillSwitch);
        Inbox = new FileInboxStore(Path.Combine(_dir.Path, "inbox"), Time);
        Tasks = new FileTaskStore(Path.Combine(_dir.Path, "tasks"));
        Coordinator = new A2ACoordinator(Registry, Envelopes, Inbox, Tasks, new FleetThread(), Taint, KillSwitch, Audit, Consults, new A2AOptions(), Time, Sessions);
        Tools = new CapybaraToolProvider(new Lazy<A2ACoordinator>(() => Coordinator), memory ?? Memory, Taint, Options, proposals);
        Configs = new SessionConfigFactory(Handlers, Usage, Tools, Options, Rules, NullLogger<SessionConfigFactory>.Instance, Secrets, broker);
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

    public WikiStore Wiki { get; }

    public CompositeMemoryStore Memory { get; }

    public InMemoryAuditLog Audit { get; } = new();

    public AgentRegistry Registry { get; }

    public FileInboxStore Inbox { get; }

    public FileTaskStore Tasks { get; }

    public FakeConsults Consults { get; } = new();

    public A2ACoordinator Coordinator { get; }

    public SessionLimiter Sessions { get; } = new(new A2AOptions());

    public string InboxRoot => Path.Combine(_dir.Path, "inbox");

    public string TaskRoot => Path.Combine(_dir.Path, "tasks");

    /// <summary>A real runtime that is never started: everything up to talking to the CLI runs.</summary>
    public Hosting.AgentRuntime Runtime(AgentDefinition agent) => new(
        agent,
        Options,
        Configs,
        Envelopes,
        Ledger,
        KillSwitch,
        Taint,
        Allowances,
        new Hosting.TurnMemory(Memory, Taint, Time, NullLogger<Hosting.TurnMemory>.Instance),
        Alerts,
        Sessions,
        Time,
        NullLogger<Hosting.AgentRuntime>.Instance);

    public SessionConfigFactory Configs { get; }

    public EnvelopeFactory Envelopes { get; }

    public CapybaraSession Session(AgentDefinition agent, SessionKind kind = SessionKind.Main, int depth = 0, string? taskId = null)
    {
        var envelope = Envelopes.CreateRoot("user", agent.Id, new Budget(5m, 50, TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5)) with { Depth = depth };
        var id = kind switch
        {
            SessionKind.Consult => SessionIds.Consult(agent.Id, Guid.NewGuid()),
            SessionKind.Task => SessionIds.Task(agent.Id, taskId ?? "routine-test"),
            SessionKind.Incognito => SessionIds.Incognito(agent.Id, "chat1"),
            _ => SessionIds.Main(agent.Id),
        };
        return new CapybaraSession(agent, id, kind, envelope, taskId);
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        Sessions.Dispose();
        KillSwitch.Dispose();
        _dir.Dispose();
    }

    /// <summary>Consults answered without a runtime.</summary>
    internal sealed class FakeConsults : IAgentTurnExecutor
    {
        public List<AgentId> Calls { get; } = [];

        public Task<ConsultReply> ConsultAsync(AgentDefinition target, string sessionId, string question, Envelope envelope, CancellationToken cancellationToken)
        {
            Calls.Add(target.Id);
            return Task.FromResult(new ConsultReply("42", Capybara.Contracts.Messaging.Taint.Clean, []));
        }
    }
}
