using Capybara.Runtime.Copilot.Hosting;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Review round 2, N2 and N3, against a session modelled on the bundled CLI: an abort on a running session emits idle
/// about 45 ms later, an abort on an idle session emits nothing, and after an error the run's idle follows shortly.
/// No run may outlive its turn, and a steering message is joined only to a run that is still active.
/// </summary>
public sealed class TurnRunTests
{
    private static readonly MessageOptions Prompt = new() { Prompt = "hello" };
    private static readonly MessageOptions Dm = new() { Prompt = "hi from the user", Mode = "immediate" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TurnTimings Timings(TimeSpan? timeout = null, TimeSpan? lateGrace = null) => new(
        timeout ?? TimeSpan.FromSeconds(10),
        AbortTimeout: TimeSpan.FromMilliseconds(300),
        IdleGrace: TimeSpan.FromMilliseconds(100),
        LateRunGrace: lateGrace ?? TimeSpan.FromMilliseconds(150),
        TimeProvider.System);

    [Fact]
    public async Task An_already_cancelled_turn_sends_nothing_and_aborts_nothing()
    {
        var session = new ModelSession();
        using var run = new TurnRun(session, steerable: true, Timings());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.SendAsync(Prompt, cancelled.Token));

        Assert.Empty(session.Calls);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task A_run_that_already_went_idle_is_not_aborted()
    {
        var session = new ModelSession { Send = s => { s.Emit(TurnEventKind.Idle); throw new IOException("lost"); } };
        using var run = new TurnRun(session, steerable: false, Timings());

        await Assert.ThrowsAsync<IOException>(() => run.SendAsync(Prompt, Ct));

        Assert.Equal(["send"], session.Calls);
    }

    [Fact]
    public async Task After_a_session_error_the_runs_own_idle_ends_it_without_an_abort()
    {
        var session = new ModelSession
        {
            Send = s =>
            {
                s.EmitLater(TurnEventKind.Idle, TimeSpan.FromMilliseconds(6));
                throw new IOException("session.error");
            },
        };
        using var run = new TurnRun(session, steerable: false, Timings());

        await Assert.ThrowsAsync<IOException>(() => run.SendAsync(Prompt, Ct));

        Assert.Equal(["send"], session.Calls);
    }

    [Fact]
    public async Task A_run_still_going_after_a_timeout_is_aborted_and_its_idle_awaited_first()
    {
        var session = new ModelSession { Send = _ => throw new TimeoutException("too slow") };
        using var run = new TurnRun(session, steerable: false, Timings());

        await Assert.ThrowsAsync<TimeoutException>(() => run.SendAsync(Prompt, Ct));

        Assert.Equal(["send", "abort"], session.Calls);
        Assert.False(session.Running);
    }

    [Fact]
    public async Task A_run_that_never_signals_idle_after_the_abort_means_the_session_must_be_discarded()
    {
        // The send failed before the CLI started anything: the abort hits an idle session, which never signals idle.
        var session = new ModelSession { Send = s => { s.Running = false; throw new IOException("rejected"); } };
        using var run = new TurnRun(session, steerable: false, Timings());

        var ex = await Assert.ThrowsAsync<TurnAbortFailedException>(() => run.SendAsync(Prompt, Ct));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal(["send", "abort"], session.Calls);
    }

    [Fact]
    public async Task Steering_joins_an_active_run_and_closes_at_its_idle()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("reply");
        Assert.Equal("reply", await send);

        Assert.False(await run.SteerAsync(Dm, Ct));
        Assert.Null(await run.FinishAsync());
        Assert.Equal(1, run.Joined);
    }

    [Fact]
    public async Task A_dry_run_is_never_steered_into()
    {
        var session = new ModelSession { SendAsyncResult = _ => new TaskCompletionSource<string?>().Task };
        using var run = new TurnRun(session, steerable: false, Timings());
        _ = run.SendAsync(Prompt, Ct);

        Assert.False(await run.SteerAsync(Dm, Ct));
        Assert.Equal(0, run.Joined);
    }

    [Fact]
    public async Task A_steer_whose_send_fails_is_not_joined()
    {
        var session = new ModelSession { SendAsyncResult = _ => new TaskCompletionSource<string?>().Task, SteerFailure = new InvalidOperationException("disposed") };
        using var run = new TurnRun(session, steerable: true, Timings());
        _ = run.SendAsync(Prompt, Ct);

        Assert.False(await run.SteerAsync(Dm, Ct));
        Assert.Equal(0, run.Joined);
    }

    [Fact]
    public async Task A_steer_that_lands_after_idle_and_starts_its_own_run_is_waited_for_and_its_reply_returned()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerArrives = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task, SteerDelivery = steerArrives.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        var steer = run.SteerAsync(Dm, Ct);           // admitted while active, still in flight...
        session.Emit(TurnEventKind.Idle);              // ...when the run ends
        finish.SetResult("turn reply");
        await send;
        steerArrives.SetResult();                      // the CLI takes it after idle and starts a run for it
        Assert.True(await steer);
        var finishing = run.FinishAsync();
        session.Emit(TurnEventKind.Other);
        session.Emit(TurnEventKind.Assistant, "reply to the DM");
        Assert.False(finishing.IsCompleted);
        session.Emit(TurnEventKind.Idle);

        Assert.Equal("reply to the DM", await finishing);
    }

    [Fact]
    public async Task A_late_steer_the_cli_merged_shows_no_activity_and_finishes_promptly()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerArrives = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task, SteerDelivery = steerArrives.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        var steer = run.SteerAsync(Dm, Ct);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        steerArrives.SetResult();
        await steer;

        Assert.Null(await run.FinishAsync());
        Assert.DoesNotContain("abort", session.Calls);
    }

    [Fact]
    public async Task A_late_run_that_overruns_is_aborted_before_the_turn_ends()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerArrives = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task, SteerDelivery = steerArrives.Task };
        using var run = new TurnRun(session, steerable: true, Timings(timeout: TimeSpan.FromMilliseconds(150)));

        var send = run.SendAsync(Prompt, Ct);
        var steer = run.SteerAsync(Dm, Ct);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        steerArrives.SetResult();
        await steer;
        session.Emit(TurnEventKind.Assistant, "partial");

        Assert.Equal("partial", await run.FinishAsync());
        Assert.Contains("abort", session.Calls);
        Assert.False(session.Running);
    }

    /// <summary>A session that behaves like the bundled CLI around runs, idle and abort.</summary>
    private sealed class ModelSession : ITurnSession
    {
        private readonly Lock _gate = new();
        private readonly List<Action<TurnEvent>> _handlers = [];
        private readonly List<string> _calls = [];

        public Func<ModelSession, string?>? Send { get; init; }

        public Func<ModelSession, Task<string?>>? SendAsyncResult { get; init; }

        public Task? SteerDelivery { get; init; }

        public Exception? SteerFailure { get; init; }

        public bool Running { get; set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public IDisposable Subscribe(Action<TurnEvent> handler)
        {
            lock (_gate)
            {
                _handlers.Add(handler);
            }

            return new Unsubscribe(() =>
            {
                lock (_gate)
                {
                    _handlers.Remove(handler);
                }
            });
        }

        public async Task<string?> SendAndWaitAsync(MessageOptions message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Record("send");
            Running = true;
            if (SendAsyncResult is not null)
            {
                return await SendAsyncResult(this).WaitAsync(cancellationToken);
            }

            return Send is null ? "ok" : Send(this);
        }

        public async Task SendAsync(MessageOptions message, CancellationToken cancellationToken)
        {
            Record("steer");
            if (SteerFailure is not null)
            {
                throw SteerFailure;
            }

            if (SteerDelivery is not null)
            {
                await SteerDelivery.WaitAsync(cancellationToken);
            }
        }

        public Task AbortAsync(CancellationToken cancellationToken)
        {
            Record("abort");
            if (Running)
            {
                EmitLater(TurnEventKind.Idle, TimeSpan.FromMilliseconds(45));
            }

            return Task.CompletedTask;
        }

        public void Emit(TurnEventKind kind, string? content = null)
        {
            Running = kind != TurnEventKind.Idle;
            Action<TurnEvent>[] handlers;
            lock (_gate)
            {
                handlers = [.. _handlers];
            }

            foreach (var handler in handlers)
            {
                handler(new TurnEvent(kind, content));
            }
        }

        public void EmitLater(TurnEventKind kind, TimeSpan delay) =>
            _ = Task.Delay(delay).ContinueWith(_ => Emit(kind), TaskScheduler.Default);

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }

        private sealed class Unsubscribe(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}
