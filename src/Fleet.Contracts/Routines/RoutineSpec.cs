using Fleet.Contracts.Agents;

namespace Fleet.Contracts.Routines;

/// <summary>A routine record (FR-RTN-02). Only the user creates, edits or enables routines (FR-RTN-04).</summary>
public sealed record RoutineSpec
{
    public required string Id { get; init; }

    public required AgentId Owner { get; init; }

    public required TriggerKind Kind { get; init; }

    /// <summary>Interval for heartbeat/cron routines. Must be at least five minutes.</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>Cron expression (Quartz syntax, seconds first) for cron routines. Takes precedence over <see cref="Interval"/>.</summary>
    public string? Cron { get; init; }

    /// <summary>Event filter for event triggers. Must be narrow (FR-RTN-07).</summary>
    public string? EventFilter { get; init; }

    public TimeOnly? ActiveFrom { get; init; }

    public TimeOnly? ActiveTo { get; init; }

    public required string Prompt { get; init; }

    public string? Skill { get; init; }

    public string? InputSource { get; init; }

    public string? ExpectedOutput { get; init; }

    public string DeliveryTarget { get; init; } = "push";

    public IReadOnlyList<string> ApprovalBoundary { get; init; } = [];

    public MissingInputBehavior OnMissingInput { get; init; } = MissingInputBehavior.SkipAndAlert;

    public decimal BudgetCreditsPerRun { get; init; } = 1m;

    public string Model { get; init; } = "auto";

    public bool Enabled { get; init; } = true;

    public bool DryRun { get; init; }
}
