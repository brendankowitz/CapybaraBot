using Capybara.Observability.Alerts;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Inbox;
using Capybara.Runtime.Copilot.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// Runs one <see cref="InboxPump"/> per agent runtime (FR-AGT-05). Registered after the runtime hosted service, so the
/// runtimes have started (or failed to) before the pumps begin; a pump for a runtime that is down retries with back-off.
/// </summary>
public sealed class InboxPumpHostedService : BackgroundService
{
    public InboxPumpHostedService(
        AgentRuntimePool pool,
        IInboxStore inbox,
        A2ACoordinator coordinator,
        IAlertSink alerts,
        InboxPumpOptions options,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(loggers);
        Pumps = [.. pool.All.Select(runtime => new InboxPump(runtime, inbox, coordinator, alerts, options, time, loggers.CreateLogger<InboxPump>()))];
        foreach (var pump in Pumps)
        {
            pump.TurnCompleted += (_, e) => TurnCompleted?.Invoke(this, e);
        }
    }

    public IReadOnlyList<InboxPump> Pumps { get; }

    /// <summary>Raised after any agent's message turn completed; the host delivers DM replies from here.</summary>
    public event EventHandler<InboxTurnCompletedEventArgs>? TurnCompleted;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Pumps.Select(pump => pump.RunAsync(stoppingToken)));
}
