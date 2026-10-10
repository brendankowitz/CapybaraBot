using Capybara.Observability.Alerts;
using Capybara.Runtime.Copilot.Supervision;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// Starts every runtime, reconciles whatever turn a previous crash interrupted, and runs the tool self-test. A runtime
/// that fails to start is reported and left for the supervisor; one that fails its self-test is paused (fail closed,
/// FR-AGT-09). Neither takes the rest of the fleet down.
/// </summary>
/// <remarks>
/// The reconciler is optional so composition that predates it keeps resolving this service; without it, start-up does
/// not report turns a crash interrupted, which is logged as Critical.
/// </remarks>
public sealed partial class AgentRuntimeHostedService(
    AgentRuntimePool pool,
    IAlertSink alerts,
    SelfTestGuard selfTest,
    TimeProvider time,
    ILogger<AgentRuntimeHostedService> logger,
    ISessionReconciler? reconciler = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (reconciler is null)
        {
            LogReconcilerMissing(logger);
        }

        foreach (var runtime in pool.Supervised)
        {
            var agent = runtime.Agent.Value;
            try
            {
                await runtime.StartAsync(cancellationToken);
            }
#pragma warning disable CA1031 // Isolate agents from each other's start failures.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogStartFailed(logger, ex, agent);
                await alerts.PublishAsync(
                    new Alert("runtime.start", AlertSeverity.Warning, $"Runtime for {agent} failed to start.", time.GetUtcNow(), agent),
                    cancellationToken);
                continue;
            }

            await ReconcileAsync(runtime, cancellationToken);
            await selfTest.VerifyAsync(runtime, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await pool.DisposeAsync();

    /// <summary>The host cannot tell a clean shutdown from a crash, so every start reconciles; a finished turn is a no-op.</summary>
    private async Task ReconcileAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        if (reconciler is null)
        {
            return;
        }

        try
        {
            await reconciler.ReconcileAsync(runtime, cancellationToken);
        }
#pragma warning disable CA1031 // Best effort; the self-test still decides whether the agent may run.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReconcileFailed(logger, ex, runtime.Agent.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Runtime {Agent} failed to start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciling runtime {Agent} on start failed")]
    private static partial void LogReconcileFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No session reconciler is registered; turns interrupted by a crash will not be reported")]
    private static partial void LogReconcilerMissing(ILogger logger);
}
