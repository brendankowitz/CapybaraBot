using Fleet.Contracts.Agents;
using Fleet.Policy.Safety;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Policy.Tests;

public sealed class KillSwitchTests
{
    [Fact]
    public void Engaging_cancels_in_flight_work()
    {
        using var killSwitch = new KillSwitch(new FakeTimeProvider());
        var token = killSwitch.FleetToken;

        killSwitch.Engage("panic");

        Assert.True(token.IsCancellationRequested);
        Assert.True(killSwitch.IsBlocked(AgentId.Parse("ea")));
    }

    [Theory]
    [InlineData(ControlSurface.Phone)]
    [InlineData(ControlSurface.Tui)]
    public void Only_the_desktop_can_release(ControlSurface surface)
    {
        using var killSwitch = new KillSwitch(new FakeTimeProvider());
        killSwitch.Engage("panic");

        Assert.Throws<InvalidOperationException>(() => killSwitch.Release(surface));
        killSwitch.Release(ControlSurface.Desktop);
        Assert.False(killSwitch.IsEngaged);
        Assert.False(killSwitch.FleetToken.IsCancellationRequested);
    }
}
