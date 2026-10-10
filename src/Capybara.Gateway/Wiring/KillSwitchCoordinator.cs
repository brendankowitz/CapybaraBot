using System.Net;
using Capybara.Connections.Client;
using Capybara.Gateway.Realtime;
using Capybara.Observability.Alerts;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Supervision;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Carries out the kill switch beyond the in-memory flag (FR-SAF-06). <see cref="KillSwitch.Engage"/> itself cancels
/// every turn in flight (<see cref="KillSwitch.FleetToken"/>) and blocks new ones; this then journals the engagement,
/// audits it, freezes the broker (revoking every grant), stops every runtime process, alerts and broadcasts. Release
/// (desktop only) unfreezes the broker and lets the supervisor restart the runtimes. Quartz is paused and resumed by
/// the scheduler's own kill switch subscription (<c>ScheduleSynchronizer</c>), which also re-pauses on start.
/// </summary>
/// <remarks>
/// Each step is isolated: a failing step raises a Critical alert and never stops the rest of the sequence. Engage and
/// release run one at a time, in the order they happened. A broker that answers "frozen, but not durably" is frozen
/// now but would forget it on restart, so it counts as engaged with a Critical alert.
/// </remarks>
public sealed partial class KillSwitchCoordinator(
    KillSwitch killSwitch,
    KillSwitchJournal journal,
    BrokerClient broker,
    RuntimeSupervisor supervisor,
    IAuditLog audit,
    IAlertSink alerts,
    ICapybaraEvents events,
    TimeProvider time,
    ILogger<KillSwitchCoordinator> logger) : IHostedService, IDisposable
{
    public const string BrokerAlertKind = "killswitch.broker";
    public const string StepFailedAlertKind = "killswitch.step_failed";
    public const string EngagedAlertKind = "killswitch.engaged";

    private readonly SemaphoreSlim _sequence = new(1, 1);
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        killSwitch.Engaged += OnEngaged;
        killSwitch.Released += OnReleased;

        // Composition re-engaged a switch that was on before the restart; finish the job (stop the runtimes it started).
        if (killSwitch.IsEngaged)
        {
            Enqueue(() => EngageAsync(killSwitch.Reason ?? "restored after restart", restored: true));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        killSwitch.Engaged -= OnEngaged;
        killSwitch.Released -= OnReleased;
        Task tail;
        lock (_gate)
        {
            tail = _tail;
        }

        await tail.WaitAsync(cancellationToken);
    }

    public void Dispose() => _sequence.Dispose();

    private void OnEngaged(object? sender, KillSwitchEventArgs e) => Enqueue(() => EngageAsync(e.Reason, restored: false));

    private void OnReleased(object? sender, KillSwitchEventArgs e) => Enqueue(ReleaseAsync);

    private void Enqueue(Func<Task> step)
    {
        lock (_gate)
        {
            _tail = RunInOrderAsync(step);
        }
    }

    private async Task RunInOrderAsync(Func<Task> step)
    {
        await _sequence.WaitAsync(CancellationToken.None);
        try
        {
            await step();
        }
#pragma warning disable CA1031 // Steps already isolate their failures; this is the last line before an unobserved task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSequenceFailed(logger, ex);
        }
        finally
        {
            _sequence.Release();
        }
    }

    private async Task EngageAsync(string reason, bool restored)
    {
        // Journal first: if capybarad dies part-way, the restart comes back engaged.
        await StepAsync("journal the engagement", () =>
        {
            journal.SaveEngaged(reason, time.GetUtcNow());
            return Task.CompletedTask;
        });
        await StepAsync("audit the engagement", async () =>
            await audit.AppendAsync(new AuditRecord("kill_switch", restored ? "restored" : "engaged", Detail: reason), CancellationToken.None));
        await FreezeBrokerAsync(reason);
        await StepAsync("stop the runtimes", () => supervisor.SuspendAsync(CancellationToken.None));
        await StepAsync("alert", async () =>
            await alerts.PublishAsync(new Alert(EngagedAlertKind, AlertSeverity.Critical, $"Kill switch engaged: {reason}. Resume from the desktop.", time.GetUtcNow()), CancellationToken.None));
        await StepAsync("broadcast", () => events.PublishKillSwitchChangedAsync(engaged: true, CancellationToken.None));
    }

    private async Task ReleaseAsync()
    {
        await StepAsync("audit the release", async () =>
            await audit.AppendAsync(new AuditRecord("kill_switch", "released", Detail: "desktop"), CancellationToken.None));
        await StepAsync("unfreeze the broker", async () =>
        {
            try
            {
                await broker.UnfreezeAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Fail safe: the broker stays frozen until unfrozen; tell the user rather than pretend.
                await alerts.PublishAsync(new Alert(BrokerAlertKind, AlertSeverity.Critical, $"The broker could not be unfrozen ({ex.Message}); connections stay disabled.", time.GetUtcNow()), CancellationToken.None);
            }
        });
        await StepAsync("resume the runtimes", () =>
        {
            supervisor.Resume();
            return Task.CompletedTask;
        });
        await StepAsync("clear the journal", () =>
        {
            // A switch engaged again meanwhile keeps its journal.
            if (!killSwitch.IsEngaged)
            {
                journal.Clear();
            }

            return Task.CompletedTask;
        });
        await StepAsync("broadcast", () => events.PublishKillSwitchChangedAsync(engaged: false, CancellationToken.None));
    }

    private async Task FreezeBrokerAsync(string reason)
    {
        try
        {
            var outcome = await broker.FreezeAsync(reason, CancellationToken.None);
            LogBrokerFrozen(logger, outcome.InFlightCalls);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.InternalServerError)
        {
            // The broker answers 500 "frozen, but not durably": every call is denied now, but a broker restart would forget.
            await PublishAsync(new Alert(BrokerAlertKind, AlertSeverity.Critical, "The broker is frozen but could not make the freeze durable; it would unfreeze if it restarted.", time.GetUtcNow()));
        }
#pragma warning disable CA1031 // Unreachable broker, timeout, bad response: alert and carry on stopping everything else.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, "freeze the broker");
            await PublishAsync(new Alert(BrokerAlertKind, AlertSeverity.Critical, $"The broker could not be frozen ({ex.GetType().Name}); connections may still be usable. Stop capybara-broker.service.", time.GetUtcNow()));
        }
    }

    private async Task StepAsync(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
#pragma warning disable CA1031 // One failed step must never stop the rest of the kill switch.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, ex, what);
            await PublishAsync(new Alert(StepFailedAlertKind, AlertSeverity.Critical, $"Kill switch: could not {what} ({ex.GetType().Name}).", time.GetUtcNow()));
        }
    }

    private async Task PublishAsync(Alert alert)
    {
        try
        {
            await alerts.PublishAsync(alert, CancellationToken.None);
        }
#pragma warning disable CA1031 // Already failing; the log line is what is left.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(logger, ex, alert.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Broker frozen for the kill switch ({InFlight} calls were already in flight)")]
    private static partial void LogBrokerFrozen(ILogger logger, int inFlight);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Kill switch step failed: {Step}")]
    private static partial void LogStepFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Kill switch sequence failed")]
    private static partial void LogSequenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish kill switch alert {Kind}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string kind);
}
