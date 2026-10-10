using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Planning;
using Capybara.Scheduling.Triggers;

namespace Capybara.Scheduling.Validation;

/// <summary>Validates user-authored routines before they can be enabled (FR-RTN-02, FR-RTN-03, FR-RTN-07).</summary>
public static class RoutineValidator
{
    /// <summary>
    /// Validates a whole routine set: duplicates, known owners, the per-agent limit and every routine on its own.
    /// Cron expressions are evaluated in <paramref name="timeZone"/> (UTC when omitted).
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyCollection<RoutineSpec> routines, IReadOnlySet<AgentId> knownAgents, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(routines);
        ArgumentNullException.ThrowIfNull(knownAgents);
        var errors = new List<string>();

        foreach (var duplicate in routines.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"routine id '{duplicate.Key}' is declared more than once.");
        }

        foreach (var group in routines.GroupBy(r => r.Owner).Where(g => g.Count() > RoutineLimits.MaxRoutinesPerAgent))
        {
            errors.Add($"{group.Key} has {group.Count()} routines; the limit is {RoutineLimits.MaxRoutinesPerAgent}.");
        }

        foreach (var routine in routines)
        {
            if (!knownAgents.Contains(routine.Owner))
            {
                errors.Add($"{routine.Id}: owner '{routine.Owner}' is not a declared agent.");
            }

            errors.AddRange(ValidateRoutine(routine, timeZone));
        }

        return errors;
    }

    /// <summary>
    /// Validates one routine on its own (FR-RTN-02, FR-RTN-03, FR-RTN-07): what it needs, the five-minute minimum interval
    /// (also for cron expressions) and narrow event filters. Owner and per-agent count need the wider context of
    /// <see cref="Validate"/> or the registry.
    /// </summary>
    public static IReadOnlyList<string> ValidateRoutine(RoutineSpec routine, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(routine.Prompt) && routine.Skill is null)
        {
            errors.Add($"{routine.Id}: needs a prompt or a skill.");
        }

        if (routine.BudgetCreditsPerRun <= 0)
        {
            errors.Add($"{routine.Id}: budget per run must be positive.");
        }

        switch (routine.Kind)
        {
            case TriggerKind.Cron when !string.IsNullOrWhiteSpace(routine.Cron):
                ValidateCron(routine, timeZone ?? TimeZoneInfo.Utc, errors);
                break;
            case TriggerKind.Heartbeat or TriggerKind.Cron when routine.Interval is null:
                errors.Add($"{routine.Id}: {routine.Kind} routines need an interval{(routine.Kind == TriggerKind.Cron ? " or a cron expression" : string.Empty)}.");
                break;
            case TriggerKind.Heartbeat or TriggerKind.Cron when routine.Interval < RoutineLimits.MinimumInterval:
                errors.Add($"{routine.Id}: interval {routine.Interval} is below the {RoutineLimits.MinimumInterval.TotalMinutes}-minute minimum.");
                break;
            case TriggerKind.Heartbeat or TriggerKind.Cron when routine.Interval > RoutineLimits.MaximumInterval:
                errors.Add($"{routine.Id}: interval {routine.Interval} is above the {RoutineLimits.MaximumInterval.TotalDays}-day maximum.");
                break;
            case TriggerKind.Event:
                try
                {
                    _ = EventFilter.Parse(routine.EventFilter ?? string.Empty);
                }
                catch (FormatException ex)
                {
                    errors.Add($"{routine.Id}: event triggers require a narrow filter: {ex.Message}");
                }

                break;
        }

        if (routine.ActiveFrom is null != routine.ActiveTo is null)
        {
            errors.Add($"{routine.Id}: active hours need both a start and an end.");
        }

        return errors;
    }

    private static void ValidateCron(RoutineSpec routine, TimeZoneInfo timeZone, List<string> errors)
    {
        if (!CronSchedule.TryParse(routine.Cron, timeZone, out var cron, out var error))
        {
            errors.Add($"{routine.Id}: cron expression '{routine.Cron}' is invalid: {error}");
            return;
        }

        if (CronSchedule.TooFrequentGap(cron) is { } gap)
        {
            errors.Add($"{routine.Id}: cron expression '{routine.Cron}' can fire {gap.TotalSeconds:0} seconds apart, below the {RoutineLimits.MinimumInterval.TotalMinutes}-minute minimum.");
        }
    }
}
