namespace Fleet.Policy.Audit;

/// <summary>One record in the append-only, hash-chained audit log (FR-SAF-09).</summary>
public sealed record AuditEntry
{
    public required long Sequence { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>e.g. <c>tool_call</c>, <c>approval</c>, <c>config_change</c>, <c>kill_switch</c>, <c>broker_call</c>.</summary>
    public required string Kind { get; init; }

    public string? Agent { get; init; }

    public string? SessionId { get; init; }

    public string? Tool { get; init; }

    public string? ArgsHash { get; init; }

    public required string Decision { get; init; }

    public string? Detail { get; init; }

    public string? TraceId { get; init; }

    public required string PreviousHash { get; init; }

    public string Hash { get; init; } = string.Empty;
}
