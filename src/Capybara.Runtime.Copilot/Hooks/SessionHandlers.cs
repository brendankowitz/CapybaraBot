using System.Text.Json;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Sessions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// The SDK callbacks every session registers. They are rebuilt and re-registered on every create <em>and</em> resume,
/// because the SDK does not persist handlers across resume (FR-AGT-03).
/// </summary>
public sealed partial class SessionHandlers(
    PolicyGate gate,
    TaintTracker taint,
    PermissionAllowances allowances,
    IUserInputBroker userInput,
    CopilotRuntimeOptions options,
    ILogger<SessionHandlers> logger)
{
    public const string UserUnavailableAnswer = "The user is not available right now. Continue without this input or stop and report what is blocked.";

    /// <summary><c>OnPreToolUse</c>: every tool call goes through the layered policy gate.</summary>
    public async Task<PreToolUseHookOutput> OnPreToolUseAsync(CapybaraSession session, PreToolUseHookInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        var call = new ToolCallContext(session.Agent, session.SessionId, input.ToolName, input.ToolArgs, session.TurnOrigin, session.Envelope.TraceId);
        GateVerdict verdict;
        try
        {
            verdict = await gate.EvaluateAsync(call);
        }
#pragma warning disable CA1031 // Fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogGateFailure(logger, ex, input.ToolName);
            return Deny("policy gate unavailable");
        }

        switch (verdict.Outcome)
        {
            case GateOutcome.Allow:
                allowances.Grant(session.SessionId);
                if (options.UntrustedInputTools.Contains(input.ToolName, StringComparer.Ordinal))
                {
                    taint.MarkTainted(session.SessionId);
                }

                return new PreToolUseHookOutput { PermissionDecision = "allow", PermissionDecisionReason = verdict.Reason };

            case GateOutcome.PendingApproval:
                return Deny($"waiting for user approval ({verdict.Approval?.Id}); do not retry until the user has decided");

            default:
                return Deny(verdict.Reason);
        }
    }

    /// <summary>
    /// Explicit, fail-closed permission handler. Never <c>ApproveAll</c>: a request is approved once only if the gate
    /// just allowed the corresponding tool call.
    /// </summary>
    public Task<PermissionDecision> OnPermissionRequestAsync(CapybaraSession session, PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(allowances.TryConsume(session.SessionId)
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject("not authorized by fleet policy"));
    }

    /// <summary><c>ask_user</c> goes to the escalation path instead of a terminal prompt.</summary>
    public async Task<UserInputResponse> OnUserInputRequestAsync(CapybaraSession session, UserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var answer = await userInput.AskAsync(session, request.Question, [.. request.Choices ?? []], CancellationToken.None);
        return new UserInputResponse { Answer = answer ?? UserUnavailableAnswer, WasFreeform = true };
    }

    private static PreToolUseHookOutput Deny(string reason) =>
        new() { PermissionDecision = "deny", PermissionDecisionReason = reason };

    [LoggerMessage(Level = LogLevel.Error, Message = "Policy gate failed for tool {Tool}; denying")]
    private static partial void LogGateFailure(ILogger logger, Exception exception, string tool);

    /// <summary>Exposed for tests: canonical args shape the gate sees.</summary>
    internal static JsonElement? Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
