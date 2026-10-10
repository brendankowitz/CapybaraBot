using System.Security.Cryptography;
using System.Text;
using Capybara.Contracts.Routines;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Jitter;

/// <summary>Deterministic ±10% jitter for routines (FR-RTN-03, NFR-07).</summary>
public static class RoutineJitter
{
    public static TimeSpan Apply(string routineId, DateTimeOffset fire, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routineId);
        if (interval < RoutineLimits.MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), $"Interval must be at least {RoutineLimits.MinimumInterval}.");
        }

        var fraction = Fraction(routineId, fire) * 2 - 1;
        return interval + (interval * (fraction * RoutineLimits.JitterFraction));
    }

    public static DateTimeOffset NextFire(RoutineSpec routine, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(routine);
        if (routine.Interval is not { } interval)
        {
            throw new InvalidOperationException("Jitter requires an interval.");
        }

        return after + Apply(routine.Id, after, interval);
    }

    private static double Fraction(string routineId, DateTimeOffset fire)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{routineId}\n{fire.UtcTicks}"));
        var value = BitConverter.ToUInt64(bytes, 0) >> 11;
        return value / (double)(1UL << 53);
    }
}
