using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// Runs the tool self-test after every start and restart and fails closed (FR-AGT-09): an agent whose runtime exposes
/// the wrong tools, or that cannot be tested at all, is paused through the <see cref="KillSwitch"/> and the user is
/// alerted. Only a human resumes it, after fixing the cause.
/// </summary>
public sealed partial class SelfTestGuard(KillSwitch killSwitch, IAlertSink alerts, TimeProvider time, ILogger<SelfTestGuard> logger)
{
    public const string AlertKind = "runtime.selftest";

    /// <summary>Returns <see langword="true"/> when the runtime passed; otherwise the agent is now paused.</summary>
    public async Task<bool> VerifyAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var agent = runtime.Agent;

        SelfTestResult result;
        try
        {
            result = Normalize(await runtime.SelfTestAsync(cancellationToken));
        }
#pragma warning disable CA1031 // A runtime that cannot be tested is treated as failing.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogSelfTestError(logger, ex, agent.Value);
            await FailAsync(agent, $"Tool self-test for {agent} could not run; the agent is paused.", cancellationToken);
            return false;
        }

        if (result.Passed)
        {
            return true;
        }

        LogSelfTestFailed(logger, agent.Value, result.Missing, result.Unexpected);
        await FailAsync(agent, $"Tool self-test failed for {agent}; the agent is paused.", cancellationToken);
        return false;
    }

    /// <summary>
    /// The allowlist is source-qualified (<c>mcp:srv-tool</c>) while the runtime reports bare names (<c>srv-tool</c>),
    /// so a qualified expectation is met by its bare form. Infrastructure tools such as tool search are optional.
    /// </summary>
    internal static SelfTestResult Normalize(SelfTestResult raw)
    {
        var unexpected = raw.Unexpected.ToHashSet(StringComparer.Ordinal);
        var missingBare = raw.Missing.Select(ToolPattern.Unqualify).ToHashSet(StringComparer.Ordinal);

        var missing = raw.Missing.Where(m => !unexpected.Contains(ToolPattern.Unqualify(m))).ToList();
        var extra = raw.Unexpected
            .Where(u => !missingBare.Contains(u) && !SessionConfigFactory.InfrastructureTools.Contains(u, StringComparer.Ordinal))
            .ToList();
        return new SelfTestResult(missing.Count == 0 && extra.Count == 0, missing, extra);
    }

    private async Task FailAsync(AgentId agent, string message, CancellationToken cancellationToken)
    {
        killSwitch.Pause(agent);
        await alerts.PublishAsync(new Alert(AlertKind, AlertSeverity.Critical, message, time.GetUtcNow(), agent.Value), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Tool self-test failed for {Agent}; pausing it. Missing: {Missing}. Unexpected: {Unexpected}")]
    private static partial void LogSelfTestFailed(ILogger logger, string agent, IReadOnlyList<string> missing, IReadOnlyList<string> unexpected);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Tool self-test for {Agent} could not run; pausing it")]
    private static partial void LogSelfTestError(ILogger logger, Exception exception, string agent);
}
