using Fleet.Contracts.Tasks;

namespace Fleet.Contracts.Tests;

public sealed class TaskStateTests
{
    [Theory]
    [InlineData(FleetTaskState.Completed, true)]
    [InlineData(FleetTaskState.Failed, true)]
    [InlineData(FleetTaskState.Canceled, true)]
    [InlineData(FleetTaskState.Rejected, true)]
    [InlineData(FleetTaskState.Working, false)]
    [InlineData(FleetTaskState.InputRequired, false)]
    public void Terminal_states(FleetTaskState state, bool terminal) => Assert.Equal(terminal, state.IsTerminal());
}
