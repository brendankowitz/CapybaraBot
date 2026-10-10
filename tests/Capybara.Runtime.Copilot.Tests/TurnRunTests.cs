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
        session.EmitUserMessage(session.SteerIds[0]);  // delivery=steering: inside the run
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("reply");
        Assert.Equal("reply", await send);

        Assert.False(await run.SteerAsync(Dm, Ct));
        Assert.Null(await run.FinishAsync(Ct));
        Assert.Equal(1, run.Joined);
        Assert.DoesNotContain("abort", session.Calls);
    }

    [Fact]
    public async Task Steering_is_closed_until_the_turn_is_sent()
    {
        var session = new ModelSession();
        using var run = new TurnRun(session, steerable: true, Timings());

        Assert.False(await run.SteerAsync(Dm, Ct));
        Assert.Empty(session.Calls);
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
    public async Task A_steer_queued_behind_the_runs_last_call_is_part_of_that_run()
    {
        // Live: a steer the loop could not inject emits user.message (delivery=queued) after assistant.turn_end, and the
        // loop goes on without an idle in between; the run's one idle covers it.
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        session.Emit(TurnEventKind.Assistant, "OK");
        session.EmitUserMessage(session.SteerIds[0]);
        session.Emit(TurnEventKind.RunActivity);
        session.Emit(TurnEventKind.Assistant, "AFTER");
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("AFTER");
        await send;

        Assert.Null(await run.FinishAsync(Ct));
        Assert.DoesNotContain("abort", session.Calls);
    }

    [Fact]
    public async Task A_steer_whose_send_returns_before_the_idle_event_but_lands_after_it_has_its_run_awaited()
    {
        // Review round 3, I4: the steer's response came back before the turn's idle was dispatched, so it looked joined;
        // its user.message (delivery=idle) after that idle shows it started a run of its own.
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));     // the response is back while no idle has been seen
        session.Emit(TurnEventKind.Idle);               // the turn's run ended before the CLI took the steer
        finish.SetResult("turn reply");
        await send;
        var finishing = run.FinishAsync(Ct);
        session.EmitUserMessage(session.SteerIds[0]);  // delivery=idle: a run of its own
        session.Emit(TurnEventKind.RunActivity);
        session.Emit(TurnEventKind.Assistant, "reply to the DM");
        await Task.Delay(50, Ct);
        Assert.False(finishing.IsCompleted);
        session.Emit(TurnEventKind.Idle);

        Assert.Equal("reply to the DM", await finishing);
        Assert.DoesNotContain("abort", session.Calls);
    }

    [Fact]
    public async Task A_steers_user_message_seen_before_its_send_returns_is_still_attributed()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        session.OnSteer = (s, id) =>
        {
            s.Emit(TurnEventKind.Idle);
            s.EmitUserMessage(id);
            s.Emit(TurnEventKind.Assistant, "late reply");
        };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        finish.SetResult("turn reply");
        await send;
        var finishing = run.FinishAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(finishing.IsCompleted);
        session.Emit(TurnEventKind.Idle);

        Assert.Equal("late reply", await finishing);
    }

    [Fact]
    public async Task A_steer_never_taken_into_a_run_finishes_after_the_grace_without_an_abort()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;

        Assert.Null(await run.FinishAsync(Ct));
        Assert.DoesNotContain("abort", session.Calls);
    }

    [Fact]
    public async Task A_late_run_that_overruns_is_aborted_before_the_turn_ends()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings(timeout: TimeSpan.FromMilliseconds(150)));

        var send = run.SendAsync(Prompt, Ct);
        await run.SteerAsync(Dm, Ct);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        session.EmitUserMessage(session.SteerIds[0]);
        session.Emit(TurnEventKind.Assistant, "partial");

        Assert.Equal("partial", await run.FinishAsync(Ct));
        Assert.Contains("abort", session.Calls);
        Assert.False(session.Running);
    }

    [Fact]
    public async Task A_late_run_is_aborted_at_once_when_the_turn_is_stopped()
    {
        // The kill switch or the turn's credit limit stop a late steer's run like the turn's own.
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings(timeout: TimeSpan.FromSeconds(30)));
        using var stop = new CancellationTokenSource();

        var send = run.SendAsync(Prompt, Ct);
        await run.SteerAsync(Dm, Ct);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        session.EmitUserMessage(session.SteerIds[0]);
        var finishing = run.FinishAsync(stop.Token);
        await Task.Delay(50, Ct);
        Assert.False(finishing.IsCompleted);

        var stopped = DateTime.UtcNow;
        await stop.CancelAsync();
        await finishing;

        Assert.Contains("abort", session.Calls);
        Assert.True(DateTime.UtcNow - stopped < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_late_run_is_still_found_and_stopped_when_the_turn_was_stopped_before_it_finished()
    {
        // Review round 4, N1: the turn is already over its credit limit (or the kill switch tripped) when it finishes; the
        // steer's user.message comes ~20 ms after the turn's idle. The grace must still find that run, then abort it.
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings(timeout: TimeSpan.FromSeconds(30)));
        using var stop = new CancellationTokenSource();

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        await stop.CancelAsync();
        session.EmitUserMessageLater(session.SteerIds[0], TimeSpan.FromMilliseconds(20));

        var started = DateTime.UtcNow;
        await run.FinishAsync(stop.Token);
        await Task.Delay(50, Ct);

        Assert.Contains("abort", session.Calls);
        Assert.False(session.Running, "the late run was left running");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_stop_during_the_grace_still_finds_the_late_run_and_aborts_it()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task };
        using var run = new TurnRun(session, steerable: true, Timings(timeout: TimeSpan.FromSeconds(30)));
        using var stop = new CancellationTokenSource();

        var send = run.SendAsync(Prompt, Ct);
        Assert.True(await run.SteerAsync(Dm, Ct));
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;
        session.EmitUserMessageLater(session.SteerIds[0], TimeSpan.FromMilliseconds(40));

        var started = DateTime.UtcNow;
        var finishing = run.FinishAsync(stop.Token);
        await Task.Delay(10, Ct);
        await stop.CancelAsync();
        await finishing;
        await Task.Delay(50, Ct);

        Assert.Contains("abort", session.Calls);
        Assert.False(session.Running, "the late run was left running");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_steer_whose_caller_gave_up_mid_send_still_has_its_late_run_awaited()
    {
        // Cancelling the caller must not abandon a send that may already have reached the CLI: the run it starts is only
        // found through the id the send returns.
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task, SteerDelivery = delivery.Task };
        using var run = new TurnRun(session, steerable: true, Timings());
        using var caller = new CancellationTokenSource();

        var send = run.SendAsync(Prompt, Ct);
        var steer = run.SteerAsync(Dm, caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => steer);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;

        var finishing = run.FinishAsync(Ct);
        delivery.SetResult();                           // the steer had reached the CLI after all
        session.EmitUserMessage(session.SteerIds[0]);  // and started a run of its own
        session.Emit(TurnEventKind.RunActivity);
        session.Emit(TurnEventKind.Assistant, "reply to the DM");
        await Task.Delay(50, Ct);
        Assert.False(finishing.IsCompleted);
        session.Emit(TurnEventKind.Idle);

        Assert.Equal("reply to the DM", await finishing);
        Assert.Equal(1, run.Joined);
    }

    [Fact]
    public async Task Steering_that_never_completes_means_the_session_must_be_discarded()
    {
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession { SendAsyncResult = _ => finish.Task, SteerDelivery = new TaskCompletionSource().Task };
        using var run = new TurnRun(session, steerable: true, Timings());

        var send = run.SendAsync(Prompt, Ct);
        var steer = run.SteerAsync(Dm, Ct);
        session.Emit(TurnEventKind.Idle);
        finish.SetResult("turn reply");
        await send;

        await Assert.ThrowsAsync<TurnAbortFailedException>(() => run.FinishAsync(Ct));
        Assert.False(steer.IsCompleted);
    }

    [Fact]
    public void Only_the_root_agents_run_events_are_the_turns()
    {
        Assert.Equal(TurnEventKind.Idle, CopilotTurnSession.Map(new SessionIdleEvent { Data = new SessionIdleData() })?.Kind);
        Assert.Equal(
            new TurnEvent(TurnEventKind.UserMessage, MessageId: "m1"),
            CopilotTurnSession.Map(new UserMessageEvent { Data = new UserMessageData { Content = "hi", MessageId = "m1" } }));
        Assert.Null(CopilotTurnSession.Map(new AssistantMessageEvent { AgentId = "sub-1", Data = new AssistantMessageData { Content = "x", MessageId = "a" } }));
        Assert.Null(CopilotTurnSession.Map(new SessionTitleChangedEvent { Data = new SessionTitleChangedData { Title = "t" } }));
    }

    /// <summary>A session that behaves like the bundled CLI around runs, idle and abort.</summary>
    private sealed class ModelSession : ITurnSession
    {
        private readonly Lock _gate = new();
        private readonly List<Action<TurnEvent>> _handlers = [];
        private readonly List<string> _calls = [];
        private readonly List<string> _steerIds = [];

        public Func<ModelSession, string?>? Send { get; init; }

        public Func<ModelSession, Task<string?>>? SendAsyncResult { get; init; }

        public Task? SteerDelivery { get; init; }

        public Exception? SteerFailure { get; init; }

        /// <summary>Runs when a steer reaches the session, before its send returns.</summary>
        public Action<ModelSession, string>? OnSteer { get; set; }

        public IReadOnlyList<string> SteerIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _steerIds];
                }
            }
        }

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

        public async Task<string> SendAsync(MessageOptions message, CancellationToken cancellationToken)
        {
            Record("steer");
            if (SteerFailure is not null)
            {
                throw SteerFailure;
            }

            var id = Guid.NewGuid().ToString();
            lock (_gate)
            {
                _steerIds.Add(id);
            }

            OnSteer?.Invoke(this, id);
            if (SteerDelivery is not null)
            {
                await SteerDelivery.WaitAsync(cancellationToken);
            }

            return id;
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

        public void Emit(TurnEventKind kind, string? content = null) => Emit(new TurnEvent(kind, content));

        public void EmitUserMessage(string id) => Emit(new TurnEvent(TurnEventKind.UserMessage, MessageId: id));

        private void Emit(TurnEvent e)
        {
            Running = e.Kind != TurnEventKind.Idle;
            Action<TurnEvent>[] handlers;
            lock (_gate)
            {
                handlers = [.. _handlers];
            }

            foreach (var handler in handlers)
            {
                handler(e);
            }
        }

        public void EmitLater(TurnEventKind kind, TimeSpan delay) =>
            _ = Task.Delay(delay).ContinueWith(_ => Emit(kind), TaskScheduler.Default);

        public void EmitUserMessageLater(string id, TimeSpan delay) =>
            _ = Task.Delay(delay).ContinueWith(_ => EmitUserMessage(id), TaskScheduler.Default);

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
