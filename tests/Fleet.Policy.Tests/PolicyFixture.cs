using System.Text.Json;
using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;
using Fleet.Policy.Approvals;
using Fleet.Policy.Gate;
using Fleet.Policy.Provenance;
using Fleet.Policy.Review;
using Fleet.Policy.Rules;
using Fleet.Policy.Safety;
using Fleet.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Policy.Tests;

internal sealed class PolicyFixture : IDisposable
{
    public PolicyFixture(IEnumerable<PolicyRule>? rules = null, IToolCallReviewer? reviewer = null)
    {
        Rules = new RulesEngine(rules ?? []);
        Approvals = new ApprovalService(Rules, new ApprovalOptions(), Time);
        KillSwitch = new KillSwitch(Time);
        Gate = new PolicyGate(Rules, Taint, Approvals, KillSwitch, Audit, reviewer);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public RulesEngine Rules { get; }

    public TaintTracker Taint { get; } = new();

    public ApprovalService Approvals { get; }

    public KillSwitch KillSwitch { get; }

    public InMemoryAuditLog Audit { get; } = new();

    public PolicyGate Gate { get; }

    public static ToolCallContext Call(AgentDefinition agent, string tool, string args = "{}", string session = "s1") =>
        new(agent, session, tool, JsonDocument.Parse(args).RootElement.Clone(), MessageOrigin.Agent);

    public void Dispose() => KillSwitch.Dispose();
}
