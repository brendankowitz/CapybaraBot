using Capybara.Contracts.Routines;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>A routine run produced output for its delivery target (<see cref="RoutineSpec.DeliveryTarget"/>).</summary>
public sealed class RoutineOutputEventArgs(RoutineSpec routine, string runId, string content) : EventArgs
{
    public RoutineSpec Routine { get; } = routine;

    public string RunId { get; } = runId;

    public string Content { get; } = content;
}
