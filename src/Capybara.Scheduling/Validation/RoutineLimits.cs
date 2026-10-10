namespace Capybara.Scheduling.Validation;

/// <summary>Routine limits (FR-RTN-03).</summary>
public static class RoutineLimits
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    public const int MaxRoutinesPerAgent = 50;

    public const int RunHistoryDepth = 20;

    public const double JitterFraction = 0.10;
}
