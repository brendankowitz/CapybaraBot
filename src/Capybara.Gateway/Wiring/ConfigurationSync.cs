using System.Threading.Channels;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Proposals;
using Capybara.Scheduling.Hosting;
using Capybara.Scheduling.Lifecycle;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Keeps live state in step with the user's config (FR-RTN-04, FR-OBS-02): routines are loaded into the scheduler and
/// budgets into the ledger at startup and after every <see cref="CapybaraConfigurationHost.Reloaded"/>. At startup it
/// also marks proposals whose target changed while capybarad was down as stale.
/// </summary>
/// <remarks>
/// This is the only caller of <see cref="RoutineConfigLoader.ApplyAsync"/>, and it acts as the human
/// (<c>RoutinePrincipal.Human("config")</c>) because a reload only ever follows a human edit, an approved proposal or a
/// lifecycle operation; never a tool, event or routine path (FR-RTN-04). Reloads are applied in order on a background
/// loop, newest wins, so the reload handler never blocks the config host's write lock. The startup load runs before
/// the scheduler starts (registration order).
/// </remarks>
public sealed partial class ConfigurationSync(
    CapybaraConfigurationHost config,
    RoutineConfigLoader routines,
    CostLedger ledger,
    ProposalService proposals,
    IAlertSink alerts,
    TimeProvider time,
    ILogger<ConfigurationSync> logger) : BackgroundService
{
    public const string FailedAlertKind = "config.sync_failed";

    private static readonly RoutinePrincipal ConfigPrincipal = RoutinePrincipal.Human("config");

    private readonly Channel<CapybaraConfiguration> _reloads = Channel.CreateBounded<CapybaraConfiguration>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        config.Reloaded += OnReloaded;
        await ApplyAsync(config.Current, cancellationToken);
        try
        {
            var stale = await proposals.ReconcileAsync(cancellationToken);
            LogProposalsReconciled(logger, stale);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReconcileFailed(logger, ex);
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        config.Reloaded -= OnReloaded;
        _reloads.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var next in _reloads.Reader.ReadAllAsync(stoppingToken))
            {
                await ApplyAsync(next, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void OnReloaded(object? sender, CapybaraConfiguration next) => _reloads.Writer.TryWrite(next);

    private async Task ApplyAsync(CapybaraConfiguration next, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var agent in next.Agents)
            {
                ledger.SetBudget(agent.Id, agent.Budget);
            }

            // A rejected routine set is alerted by the loader itself and the previous routines keep running.
            var result = await routines.ApplyAsync(next.Routines, next.Agents.Select(a => a.Id).ToHashSet(), ConfigPrincipal, cancellationToken);
            LogRoutinesApplied(logger, result.Applied, result.Added.Count, result.Updated.Count, result.Removed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSyncFailed(logger, ex);
            await alerts.PublishAsync(new Alert(FailedAlertKind, AlertSeverity.Critical, $"Applying the configuration to the scheduler failed ({ex.GetType().Name}); routines may be stale.", time.GetUtcNow()), CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Routines applied={Applied}: {Added} added, {Updated} updated, {Removed} removed")]
    private static partial void LogRoutinesApplied(ILogger logger, bool applied, int added, int updated, int removed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Stale} pending proposals went stale while capybarad was down")]
    private static partial void LogProposalsReconciled(ILogger logger, int stale);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciling proposals at startup failed")]
    private static partial void LogReconcileFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Applying the configuration failed")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception);
}
