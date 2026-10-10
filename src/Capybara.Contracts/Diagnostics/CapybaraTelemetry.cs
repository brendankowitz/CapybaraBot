using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Capybara.Contracts.Diagnostics;

/// <summary>
/// The fleet's OpenTelemetry sources (FR-OBS-01). The SDK emits LLM spans; these add gate decisions, approvals, A2A
/// hops, routines and connector calls into the same trace. Content is never put on spans (FR-OBS-04).
/// </summary>
public static class CapybaraTelemetry
{
    /// <summary>Prefix for every fleet source and meter, for <c>AddSource("Capybara.*")</c>.</summary>
    public const string Prefix = "Capybara";

    public static readonly ActivitySource Policy = new("Capybara.Policy");

    public static readonly ActivitySource Orchestration = new("Capybara.Orchestration");

    public static readonly ActivitySource Runtime = new("Capybara.Runtime");

    public static readonly ActivitySource Scheduling = new("Capybara.Scheduling");

    public static readonly ActivitySource Connections = new("Capybara.Connections");

    public static readonly Meter Meter = new("Capybara");

    public static readonly Counter<long> GateDecisions = Meter.CreateCounter<long>("capybara.gate.decisions", description: "Tool-call gate decisions by outcome and layer.");

    public static readonly Counter<long> EgressDenials = Meter.CreateCounter<long>("capybara.egress.denials", description: "Blocked egress attempts.");

    public static readonly Counter<long> ApprovalsExpired = Meter.CreateCounter<long>("capybara.approvals.expired", description: "Approvals that expired unanswered.");

    /// <summary>Starts a span whose parent is the W3C <c>traceparent</c> carried in an envelope, when there is one.</summary>
    public static Activity? StartFromTraceParent(ActivitySource source, string name, string? traceParent, ActivityKind kind = ActivityKind.Internal) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parent)
            ? source.StartActivity(name, kind, parent)
            : source.StartActivity(name, kind);
}
