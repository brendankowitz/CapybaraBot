namespace Capybara.Contracts.Tasks;

/// <summary>A2A-shaped task states.</summary>
public enum FleetTaskState
{
    Submitted,
    Working,
    InputRequired,
    Completed,
    Failed,
    Canceled,
    Rejected,
}

public static class FleetTaskStateExtensions
{
    public static bool IsTerminal(this FleetTaskState state) =>
        state is FleetTaskState.Completed or FleetTaskState.Failed or FleetTaskState.Canceled or FleetTaskState.Rejected;
}
