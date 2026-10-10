using Fleet.Observability.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Runtime.Copilot.Hosting;

/// <summary>
/// Starts every runtime and runs the tool self-test. A runtime that fails to start or fails its self-test is
/// reported and left for the supervisor; it never takes the rest of the fleet down.
/// </summary>
public sealed partial class AgentRuntimeHostedService(
    AgentRuntimePool pool,
    IAlertSink alerts,
    TimeProvider time,
    ILogger<AgentRuntimeHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in pool.All)
        {
            var agent = runtime.Agent.Value;
            try
            {
                await runtime.StartAsync(cancellationToken);
                var test = await runtime.SelfTestAsync(cancellationToken);
                if (!test.Passed)
                {
                    LogSelfTestFailed(logger, agent, test.Missing, test.Unexpected);
                    await alerts.PublishAsync(
                        new Alert("runtime.selftest", AlertSeverity.Critical, $"Tool self-test failed for {agent}.", time.GetUtcNow(), agent),
                        cancellationToken);
                }
            }
#pragma warning disable CA1031 // Isolate agents from each other's start failures.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogStartFailed(logger, ex, agent);
                await alerts.PublishAsync(
                    new Alert("runtime.start", AlertSeverity.Warning, $"Runtime for {agent} failed to start.", time.GetUtcNow(), agent),
                    cancellationToken);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await pool.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Error, Message = "Runtime {Agent} failed to start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string agent);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Tool self-test failed for {Agent}. Missing: {Missing}. Unexpected: {Unexpected}")]
    private static partial void LogSelfTestFailed(ILogger logger, string agent, IReadOnlyList<string> missing, IReadOnlyList<string> unexpected);
}
