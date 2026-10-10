using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fleet.Contracts.Diagnostics;

/// <summary>
/// The fleet's OpenTelemetry sources (FR-OBS-01). The SDK emits LLM spans; these add gate decisions, approvals, A2A
/// hops, routines and connector calls into the same trace. Content is never put on spans (FR-OBS-04).
/// </summary>
public static class FleetTelemetry
{
    /// <summary>Prefix for every fleet source and meter, for <c>AddSource("Fleet.*")</c>.</summary>
    public const string Prefix = "Fleet";

    public static readonly ActivitySource Policy = new("Fleet.Policy");

    public static readonly ActivitySource Orchestration = new("Fleet.Orchestration");

    public static readonly ActivitySource Runtime = new("Fleet.Runtime");

    public static readonly ActivitySource Scheduling = new("Fleet.Scheduling");

    public static readonly ActivitySource Connections = new("Fleet.Connections");

    public static readonly Meter Meter = new("Fleet");

    public static readonly Counter<long> GateDecisions = Meter.CreateCounter<long>("fleet.gate.decisions", description: "Tool-call gate decisions by outcome and layer.");

    public static readonly Counter<long> EgressDenials = Meter.CreateCounter<long>("fleet.egress.denials", description: "Blocked egress attempts.");

    public static readonly Counter<long> ApprovalsExpired = Meter.CreateCounter<long>("fleet.approvals.expired", description: "Approvals that expired unanswered.");

    /// <summary>Starts a span whose parent is the W3C <c>traceparent</c> carried in an envelope, when there is one.</summary>
    public static Activity? StartFromTraceParent(ActivitySource source, string name, string? traceParent, ActivityKind kind = ActivityKind.Internal) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parent)
            ? source.StartActivity(name, kind, parent)
            : source.StartActivity(name, kind);
}
