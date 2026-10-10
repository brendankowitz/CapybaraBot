using Capybara.Runtime.Copilot.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// FR-AGT-06: replacing an agent's CLI for an edited definition never lets a session turn run on the old process after
/// the swap began, and a turn still running when the drain timeout passes is aborted and reported, not killed silently.
/// </summary>
public sealed class SessionTurnGateTests : IDisposable
{
    private readonly SessionTurnGate _gate = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _gate.Dispose();

    [Fact]
    public async Task No_turn_starts_on_the_old_process_once_a_replacement_is_waiting()
    {
        var process = 1;
        var inFlight = await _gate.EnterAsync(Ct);
        var startedOn = process;

        var replacement = _gate.RunExclusiveAsync(() => { process = 2; return Task.CompletedTask; }, TimeSpan.FromMinutes(1), TimeProvider.System, Ct);
        var next = _gate.EnterAsync(Ct).AsTask();
        await Task.Delay(50, Ct);

        Assert.False(next.IsCompleted);
        Assert.Equal(1, process);

        inFlight.Dispose();
        Assert.Equal(0, await replacement);
        using var admitted = await next;
        Assert.Equal(2, process);
        Assert.Equal(1, startedOn);
        Assert.False(inFlight.WasAborted);
        Assert.False(admitted.WasAborted);
    }

    [Fact]
    public async Task A_turn_still_running_at_the_drain_timeout_is_aborted_before_the_process_is_replaced()
    {
        var time = new FakeTimeProvider();
        var order = new List<string>();
        var lease = await _gate.EnterAsync(Ct);
        var turn = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, lease.AbortToken);
            }
            catch (OperationCanceledException)
            {
                lock (order)
                {
                    order.Add("turn aborted");
                }
            }
            finally
            {
                lease.Dispose();
            }
        }, Ct);

        var replacement = _gate.RunExclusiveAsync(
            () =>
            {
                lock (order)
                {
                    order.Add("process replaced");
                }

                return Task.CompletedTask;
            },
            TimeSpan.FromMinutes(1),
            time,
            Ct);
        time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50, Ct);
        Assert.False(replacement.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, await replacement.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await turn;
        Assert.True(lease.WasAborted);
        Assert.Equal(["turn aborted", "process replaced"], order);

        // Later turns run on the new process with a fresh, untripped abort token.
        using var after = await _gate.EnterAsync(Ct);
        Assert.False(after.WasAborted);
    }

    [Fact]
    public async Task A_failed_replacement_still_reopens_the_gate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _gate.RunExclusiveAsync(() => throw new InvalidOperationException("start failed"), TimeSpan.FromMinutes(1), TimeProvider.System, Ct));

        using var lease = await _gate.EnterAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(lease.WasAborted);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_counts_once()
    {
        var lease = await _gate.EnterAsync(Ct);
        var other = await _gate.EnterAsync(Ct);
        lease.Dispose();
        lease.Dispose();

        var replacement = _gate.RunExclusiveAsync(() => Task.CompletedTask, TimeSpan.FromMinutes(1), TimeProvider.System, Ct);
        await Task.Delay(50, Ct);
        Assert.False(replacement.IsCompleted);

        other.Dispose();
        Assert.Equal(0, await replacement);
    }
}
