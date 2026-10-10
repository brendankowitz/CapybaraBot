namespace Fleet.Scheduling.History;

public enum RoutineRunOutcome
{
    Succeeded,
    Failed,
    Skipped,
    Refused,
    OverBudget,
    Missed,
}

/// <summary>A run record with evidence (FR-RTN-03, FR-OBS-06).</summary>
public sealed record RoutineRun(
    string RoutineId,
    DateTimeOffset StartedAt,
    RoutineRunOutcome Outcome,
    string? Summary = null,
    decimal CreditsSpent = 0,
    IReadOnlyList<string>? Evidence = null);
