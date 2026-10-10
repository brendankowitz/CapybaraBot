using Capybara.Observability.Alerts;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Inbox;
using Capybara.Runtime.Copilot.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Inbox;

/// <summary>
/// Runs one <see cref="InboxPump"/> per agent runtime (FR-AGT-05) and supervises it: a pump that ever faults is
/// alerted and restarted with exponential back-off, so no agent's inbox stops for good (NFR-02). Registered after the
/// runtime hosted service, so runtimes have started (or failed to) before the pumps begin.
/// </summary>
public sealed partial class InboxPumpHostedService : BackgroundService
{
    public const string RestartAlertKind = "inbox.pump_restarted";

    private readonly IAlertSink _alerts;
    private readonly InboxPumpOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InboxPumpHostedService> _logger;

    public InboxPumpHostedService(
        AgentRuntimePool pool,
        IInboxStore inbox,
        A2ACoordinator coordinator,
        IAlertSink alerts,
        InboxPumpOptions options,
        TimeProvider time,
        ILoggerFactory loggers,
        CopilotRoutineExecutor? routines = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(loggers);
        _alerts = alerts;
        _options = options;
        _time = time;
        _logger = loggers.CreateLogger<InboxPumpHostedService>();
        Pumps = [.. pool.All.Select(runtime => new InboxPump(runtime, inbox, coordinator, alerts, options, time, loggers.CreateLogger<InboxPump>(), routines))];
        foreach (var pump in Pumps)
        {
            pump.TurnCompleted += (_, e) => TurnCompleted?.Invoke(this, e);
        }
    }

    public IReadOnlyList<InboxPump> Pumps { get; }

    /// <summary>Raised after any agent's message turn completed; the host delivers DM replies from here.</summary>
    public event EventHandler<InboxTurnCompletedEventArgs>? TurnCompleted;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Pumps.Select(pump => SuperviseAsync(pump, stoppingToken)));

    private async Task SuperviseAsync(InboxPump pump, CancellationToken stoppingToken)
    {
        var delay = _options.InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pump.RunAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A faulted pump is restarted, never left dead.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPumpFaulted(_logger, ex, pump.Agent.Value, delay);
                await PublishAsync(new Alert(RestartAlertKind, AlertSeverity.Warning, $"The inbox of {pump.Agent} stopped unexpectedly and is being restarted.", _time.GetUtcNow(), pump.Agent.Value));
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 < _options.MaxRetryDelay ? delay * 2 : _options.MaxRetryDelay;
        }
    }

    private async Task PublishAsync(Alert alert)
    {
        try
        {
            await _alerts.PublishAsync(alert, CancellationToken.None);
        }
#pragma warning disable CA1031 // Restarting matters more than the alert.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The inbox pump of {Agent} faulted; restarting in {Delay}")]
    private static partial void LogPumpFaulted(ILogger logger, Exception exception, string agent, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish the inbox restart alert")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception);
}
