using Capybara.Contracts.Routines;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Planning;

/// <summary>Computes jittered next-fire times that respect active hours (FR-RTN-01, FR-RTN-03).</summary>
public sealed class RoutinePlanner(Random random)
{
    /// <summary>Returns the next fire time after <paramref name="after"/>, or <see langword="null"/> for event triggers.</summary>
    public DateTimeOffset? NextFire(RoutineSpec routine, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.Kind == TriggerKind.Event || routine.Interval is not { } interval)
        {
            return null;
        }

        var jitter = (random.NextDouble() * 2 - 1) * RoutineLimits.JitterFraction;
        var next = after + interval + (interval * jitter);
        return AlignToActiveHours(routine, next);
    }

    public static bool IsWithinActiveHours(RoutineSpec routine, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.ActiveFrom is not { } from || routine.ActiveTo is not { } to)
        {
            return true;
        }

        var time = TimeOnly.FromTimeSpan(at.TimeOfDay);
        return from <= to ? time >= from && time < to : time >= from || time < to;
    }

    private static DateTimeOffset AlignToActiveHours(RoutineSpec routine, DateTimeOffset candidate)
    {
        if (IsWithinActiveHours(routine, candidate) || routine.ActiveFrom is not { } from)
        {
            return candidate;
        }

        var start = new DateTimeOffset(candidate.Date + from.ToTimeSpan(), candidate.Offset);
        return start > candidate ? start : start.AddDays(1);
    }
}
