using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Triggers;

namespace Capybara.Scheduling.Validation;

/// <summary>Validates user-authored routines before they can be enabled (FR-RTN-02, FR-RTN-03, FR-RTN-07).</summary>
public static class RoutineValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyCollection<RoutineSpec> routines, IReadOnlySet<AgentId> knownAgents)
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
                case TriggerKind.Heartbeat or TriggerKind.Cron when routine.Interval is null:
                    errors.Add($"{routine.Id}: {routine.Kind} routines need an interval.");
                    break;
                case TriggerKind.Heartbeat or TriggerKind.Cron when routine.Interval < RoutineLimits.MinimumInterval:
                    errors.Add($"{routine.Id}: interval {routine.Interval} is below the {RoutineLimits.MinimumInterval.TotalMinutes}-minute minimum.");
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
        }

        return errors;
    }
}
