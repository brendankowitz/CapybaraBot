using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Capybara.Policy.Gate;
using Capybara.Policy.Provenance;
using Capybara.Policy.Redaction;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// The SDK callbacks every session registers. They are rebuilt and re-registered on every create <em>and</em> resume,
/// because the SDK does not persist handlers across resume (FR-AGT-03).
/// </summary>
/// <remarks>
/// The redactor is shared with <see cref="SessionConfigFactory"/>, which registers every provider key and MCP token it
/// resolves, so those literals are scrubbed from tool output. When the host registers no redactor, a private one still
/// removes well-known token shapes.
/// </remarks>
public sealed partial class SessionHandlers(
    PolicyGate gate,
    TaintTracker taint,
    PermissionAllowances allowances,
    IUserInputBroker userInput,
    CopilotRuntimeOptions options,
    ILogger<SessionHandlers> logger,
    IAlertSink alerts,
    TimeProvider time,
    SecretRedactor? redactor = null,
    ApprovalTurnStore? approvalTurns = null,
    RuntimeActivity? activity = null)
{
    public const string UserUnavailableAnswer = "The user is not available right now. Continue without this input or stop and report what is blocked.";

    public const string SecretInErrorAlertKind = "runtime.secret_in_tool_error";

    internal const string OutputWithheld = "tool output withheld: redaction failed";

    internal const string SecretInErrorContext =
        "The previous tool error contained a secret or credential. It has been withheld from you. Do not repeat, quote, " +
        "store or send any part of it, and do not try to recover it.";

    private static readonly IReadOnlyList<string> ConfirmChoices = ["Accept", "Decline"];

    /// <summary>Tools a routine dry run never runs: they act outside the test (FR-RTN-08).</summary>
    private static readonly HashSet<string> DryRunForbidden = new(
        [.. FleetTools.AgentToAgent, .. FleetTools.TaskProtocol, FleetTools.AnswerTask, .. FleetTools.MemoryWrite, FleetTools.ProposeChange],
        StringComparer.Ordinal);

    internal SecretRedactor Redactor { get; } = redactor ?? new SecretRedactor();

    /// <summary>
    /// <c>OnPreToolUse</c>: a call carrying a secret is denied outright (it could only be exfiltration of a leaked value,
    /// FR-SAF-08); the task protocol tools of a session's own task are host-authorized; every other call goes through
    /// the layered policy gate. Every decision is recorded on the turn as evidence (FR-OBS-06).
    /// </summary>
    public async Task<PreToolUseHookOutput> OnPreToolUseAsync(CapybaraSession session, PreToolUseHookInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        var turn = session.Turn;

        // Checked before the gate so no approval request is ever raised with the secret in its arguments.
        if (ContainsSecret(input.ToolArgs))
        {
            LogSecretInArguments(logger, input.ToolName, session.SessionId);
            turn.RecordAction($"{input.ToolName}: denied (secret in arguments)");
            return Deny("the arguments contain a secret or credential; secrets must never be passed to tools");
        }

        // FR-A2A-06: the tools are removed at max depth; this also covers a live session whose tools could not be swapped.
        if (session.Envelope.IsAtMaxDepth && FleetTools.AgentToAgent.Contains(input.ToolName, StringComparer.Ordinal))
        {
            turn.RecordAction($"{input.ToolName}: denied (depth limit)");
            return Deny($"depth limit reached ({Contracts.Messaging.Envelope.MaxDepth}); agent-to-agent calls are not available in this turn");
        }

        // FR-RTN-08: checked before the protocol bypass, so no dry run reaches another agent, memory or a proposal.
        if (session.DryRun && DryRunForbidden.Contains(input.ToolName))
        {
            turn.RecordDeniedApproval(input.ToolName);
            turn.RecordAction($"{input.ToolName}: denied ({PolicyGate.DryRunLayer})");
            return Deny("dry run: tools that reach other agents, change memory or propose changes are not run in a test");
        }

        if (IsProtocolTool(session, input.ToolName))
        {
            // These only move data inside the session's own task tree; the coordinator authorizes and audits them.
            Allow(session, input.ToolName);
            turn.RecordAction($"{input.ToolName}: allowed (task protocol)");
            return new PreToolUseHookOutput { PermissionDecision = "allow", PermissionDecisionReason = "task protocol" };
        }

        var call = new ToolCallContext(session.Agent, session.SessionId, input.ToolName, input.ToolArgs, session.TurnOrigin, session.Envelope.TraceId, session.DryRun, session.ApprovalBoundary);
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
            turn.RecordAction($"{input.ToolName}: denied (policy gate unavailable)");
            return Deny("policy gate unavailable");
        }

        switch (verdict.Outcome)
        {
            case GateOutcome.Allow:
                Allow(session, input.ToolName);
                turn.RecordAction($"{input.ToolName}: allowed ({verdict.Layer})");

                // Taint before the call runs, so parallel calls in the same turn are already judged as tainted.
                if (IsUntrustedSource(session, input.ToolName))
                {
                    taint.MarkTainted(session.SessionId);
                }

                return new PreToolUseHookOutput { PermissionDecision = "allow", PermissionDecisionReason = verdict.Reason };

            case GateOutcome.PendingApproval:
                turn.RecordAction($"{input.ToolName}: waiting for approval");
                if (verdict.Approval is { } approval)
                {
                    turn.RecordPendingApproval(approval.Id);

                    // The resume after the decision continues this turn's chain, not a fresh root (FR-A2A-05/07).
                    approvalTurns?.Save(approval.Id, new ApprovalTurnContext(session.Envelope, session.TurnOrigin, session.TurnFrom, session.RoutineId, session.ApprovalBoundary));
                }

                return Deny($"waiting for user approval ({verdict.Approval?.Id}); do not retry until the user has decided");

            default:
                if (verdict.Layer == PolicyGate.DryRunLayer)
                {
                    turn.RecordDeniedApproval(input.ToolName);
                }
                else if (input.ToolArgs is { } denied && AnyString(denied, IsUrl))
                {
                    // A refused call that would have reached a URL: a burst of these is an exfiltration signal (FR-OBS-05).
                    activity?.OnEgressDenied(session.Agent.Id);
                }

                turn.RecordAction($"{input.ToolName}: denied ({verdict.Layer})");
                return Deny(verdict.Reason);
        }
    }

    /// <summary>
    /// Wraps a host-invoked function so it only runs after the gate allowed a call to it in <paramref name="session"/>
    /// (see <see cref="GatedFunction"/>). Used for broker tools.
    /// </summary>
    internal AIFunction GateInvocation(CapybaraSession session, AIFunction function) =>
        new GatedFunction(function, session.SessionId, allowances);

    /// <summary>
    /// <c>complete_task</c> and <c>request_input</c> in the session of the task they act on, and <c>answer_task</c>
    /// wherever the host offered it. Never in consults, which cannot own or delegate work.
    /// </summary>
    private static bool IsProtocolTool(CapybaraSession session, string tool) => session.Kind switch
    {
        SessionKind.Consult => false,
        SessionKind.Task when session.TaskId is not null && FleetTools.TaskProtocol.Contains(tool, StringComparer.Ordinal) => true,
        _ => tool == FleetTools.AnswerTask && SessionConfigFactory.AvailableToolsFor(session).Contains(FleetTools.AnswerTask, StringComparer.Ordinal),
    };

    /// <summary>
    /// A gate allow becomes exactly one credit of the kind the call will consume: a broker tool's wrapper consumes an
    /// invocation credit; a built-in or MCP tool raises one permission request. Host tools need neither (the runtime
    /// asks no permission for custom tools), so granting them a permission credit would leave it lying around for an
    /// unrelated request.
    /// </summary>
    private void Allow(CapybaraSession session, string tool)
    {
        if (session.IsGatedTool(tool))
        {
            allowances.GrantInvocation(session.SessionId, tool);
        }
        else if (!session.IsHostTool(tool))
        {
            allowances.Grant(session.SessionId);
        }
    }

    /// <summary>
    /// <c>OnPostToolUse</c>: the result is about to enter model context. Secrets are redacted from it (FR-SAF-08) and a
    /// result from an untrusted source taints the session (FR-SAF-03). Built-in, MCP and fleet tools all pass here.
    /// Fails closed: if redaction itself fails, the output is withheld from the model.
    /// </summary>
    /// <returns><see langword="null"/> when the result passes through unchanged.</returns>
    public Task<PostToolUseHookOutput?> OnPostToolUseAsync(CapybaraSession session, PostToolUseHookInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        if (IsUntrustedSource(session, input.ToolName))
        {
            taint.MarkTainted(session.SessionId);
            session.Turn.RecordSource(input.ToolName);
        }

        try
        {
            if (input.ToolResult is not { } result || Redact(result) is not { } redacted)
            {
                return Task.FromResult<PostToolUseHookOutput?>(null);
            }

            LogRedacted(logger, input.ToolName, session.SessionId);
            return Task.FromResult<PostToolUseHookOutput?>(new PostToolUseHookOutput { ModifiedResult = redacted });
        }
#pragma warning disable CA1031 // Fail closed: unredacted output must never reach the model.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRedactionFailed(logger, ex, input.ToolName, session.SessionId);
            return Task.FromResult<PostToolUseHookOutput?>(new PostToolUseHookOutput { SuppressOutput = true, AdditionalContext = OutputWithheld });
        }
    }

    /// <summary>
    /// <c>OnPostToolUseFailure</c>: the SDK cannot replace a failed call's error text, so a secret in it cannot be
    /// redacted. The session is tainted, the user is alerted (tool name only, never content) and the model is told not
    /// to repeat the value; the pre-tool hook then denies any call that tries to send it on (FR-SAF-08).
    /// </summary>
    /// <returns><see langword="null"/> when the error carries no secret.</returns>
    public async Task<PostToolUseFailureHookOutput?> OnPostToolUseFailureAsync(CapybaraSession session, PostToolUseFailureHookInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        bool leaked;
        try
        {
            leaked = !string.IsNullOrEmpty(input.Error) && Redactor.TryRedact(input.Error, out _);
        }
#pragma warning disable CA1031 // Fail closed: an error that cannot be checked is treated as leaking.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRedactionFailed(logger, ex, input.ToolName, session.SessionId);
            leaked = true;
        }

        if (!leaked)
        {
            return null;
        }

        taint.MarkTainted(session.SessionId);
        LogSecretInError(logger, input.ToolName, session.SessionId);
        var agent = session.Agent.Id.Value;
        try
        {
            await alerts.PublishAsync(
                new Alert(SecretInErrorAlertKind, AlertSeverity.Critical, $"A secret appeared in the error output of tool {input.ToolName} for {agent}; the session is tainted.", time.GetUtcNow(), agent),
                CancellationToken.None);
        }
#pragma warning disable CA1031 // The model must still be warned when the alert channel is down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(logger, ex, session.SessionId);
        }

        return new PostToolUseFailureHookOutput { AdditionalContext = SecretInErrorContext };
    }
    /// <summary>
    /// Explicit, fail-closed permission handler. Never <c>ApproveAll</c>: a request is approved once only if the gate
    /// just allowed the corresponding tool call.
    /// </summary>
#pragma warning disable GHCP001 // PermissionDecision is evaluation-only, but it is the SDK's only fail-closed permission API.
    public Task<PermissionDecision> OnPermissionRequestAsync(CapybaraSession session, PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(allowances.TryConsume(session.SessionId)
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject("not authorized by fleet policy"));
    }
#pragma warning restore GHCP001

    /// <summary><c>ask_user</c> goes to the escalation path instead of a terminal prompt.</summary>
    public async Task<UserInputResponse> OnUserInputRequestAsync(CapybaraSession session, UserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var answer = await userInput.AskAsync(session, request.Question, [.. request.Choices ?? []], CancellationToken.None);
        return new UserInputResponse { Answer = answer ?? UserUnavailableAnswer, WasFreeform = true };
    }

    /// <summary>
    /// Elicitation from an MCP server or the runtime (FR-AGT-03). A single free-text or single-choice field is routed
    /// to the human through <see cref="IUserInputBroker"/>; anything else is declined, as are requests from MCP servers
    /// marked untrusted or not declared for the agent (their text is attacker-controlled and must not be put in front of
    /// the user as if it came from the fleet). URL elicitations are always declined: no server may steer the user to a
    /// link (OAuth consent goes through the broker instead).
    /// </summary>
#pragma warning disable GHCP001 // UIElicitationResponseAction is evaluation-only; it is the only way to answer an elicitation.
    public async Task<ElicitationResult> OnElicitationRequestAsync(CapybaraSession session, ElicitationContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        var fromRuntime = string.IsNullOrWhiteSpace(context.ElicitationSource);
        var source = fromRuntime ? "the runtime" : context.ElicitationSource!;
        var trustedSource = fromRuntime
            || session.Agent.McpServers.Any(s => !s.Untrusted && string.Equals(s.Name, source, StringComparison.Ordinal));
        if (!trustedSource || context.Mode == ElicitationRequestedMode.Url || !TryReadSingleField(context.RequestedSchema, out var field, out var choices))
        {
            LogElicitationDeclined(logger, source, session.SessionId);
            return new ElicitationResult { Action = UIElicitationResponseAction.Decline };
        }

        var answer = await userInput.AskAsync(session, $"[{source}] {context.Message}", choices, CancellationToken.None);
        if (answer is null)
        {
            return new ElicitationResult { Action = UIElicitationResponseAction.Cancel };
        }

        if (field is null)
        {
            // A bare confirmation: only an explicit "Accept" is consent.
            return new ElicitationResult
            {
                Action = string.Equals(answer, ConfirmChoices[0], StringComparison.OrdinalIgnoreCase)
                    ? UIElicitationResponseAction.Accept
                    : UIElicitationResponseAction.Decline,
            };
        }

        // A free-form reply to a single-choice field is not one of the server's options; never forward it.
        if (choices.Count > 0 && !choices.Contains(answer, StringComparer.Ordinal))
        {
            LogElicitationDeclined(logger, source, session.SessionId);
            return new ElicitationResult { Action = UIElicitationResponseAction.Decline };
        }

        return new ElicitationResult
        {
            Action = UIElicitationResponseAction.Accept,
            Content = new Dictionary<string, object> { [field] = answer },
        };
    }
#pragma warning restore GHCP001

    /// <summary>Tools configured as untrusted, and every tool of an MCP server marked untrusted.</summary>
    private bool IsUntrustedSource(CapybaraSession session, string tool) =>
        options.UntrustedInputTools.Contains(tool, StringComparer.Ordinal)
        || session.Agent.McpServers.Any(s => s.Untrusted && s.RuntimeToolNames().Contains(tool, StringComparer.Ordinal));

    /// <summary>
    /// Fails closed: arguments that cannot be checked are treated as carrying a secret. Only high-precision matches
    /// count (<see cref="SecretRedactor.ContainsSecret"/>); loose shapes like <c>apiKey = ...</c> are ordinary code and
    /// are left to the gate. Decoded string values and property names are checked, not raw JSON, so a secret whose
    /// characters JSON escapes is still found.
    /// </summary>
    private bool ContainsSecret(JsonElement? args)
    {
        if (args is not { } value)
        {
            return false;
        }

        try
        {
            return AnyString(value, Redactor.ContainsSecret);
        }
#pragma warning disable CA1031 // Fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogArgumentCheckFailed(logger, ex);
            return true;
        }
    }

    private static bool AnyString(JsonElement element, Func<string, bool> predicate) => element.ValueKind switch
    {
        JsonValueKind.String => predicate(element.GetString()!),
        JsonValueKind.Array => element.EnumerateArray().Any(e => AnyString(e, predicate)),
        JsonValueKind.Object => element.EnumerateObject().Any(p => predicate(p.Name) || AnyString(p.Value, predicate)),
        _ => false,
    };

    private static bool IsUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the redacted result, or <see langword="null"/> when nothing needed redacting.</summary>
    private JsonElement? Redact(JsonElement result)
    {
        // Redact the raw JSON rather than individual string values, so key/value shapes such as "api_key": "..."
        // are caught even though the key and value are separate nodes.
        var raw = result.GetRawText();
        if (!Redactor.TryRedact(raw, out var redacted))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(redacted);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // The placeholder never breaks JSON in practice; if it ever does, the model still gets redacted text.
            return JsonSerializer.SerializeToElement(redacted);
        }
    }

    private static bool TryReadSingleField(ElicitationSchema? schema, out string? field, out IReadOnlyList<string> choices)
    {
        field = null;
        choices = ConfirmChoices;
        var properties = schema?.Properties;
        if (properties is null || properties.Count == 0)
        {
            return true;
        }

        if (properties.Count > 1)
        {
            return false;
        }

        var (name, definition) = properties.Single();
        if (definition is not JsonElement { ValueKind: JsonValueKind.Object } property
            || !property.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "string")
        {
            return false;
        }

        field = name;
        choices = property.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];
        return true;
    }

    private static PreToolUseHookOutput Deny(string reason) =>
        new() { PermissionDecision = "deny", PermissionDecisionReason = reason };

    [LoggerMessage(Level = LogLevel.Error, Message = "Policy gate failed for tool {Tool}; denying")]
    private static partial void LogGateFailure(ILogger logger, Exception exception, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redacted secrets from {Tool} output in session {SessionId}")]
    private static partial void LogRedacted(ILogger logger, string tool, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Declined elicitation from {Source} in session {SessionId}")]
    private static partial void LogElicitationDeclined(ILogger logger, string source, string sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Redaction failed for {Tool} output in session {SessionId}; output withheld")]
    private static partial void LogRedactionFailed(ILogger logger, Exception exception, string tool, string sessionId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Error output of {Tool} in session {SessionId} contained a secret; session tainted")]
    private static partial void LogSecretInError(ILogger logger, string tool, string sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not publish the secret-leak alert for session {SessionId}")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Denied {Tool} in session {SessionId}: its arguments contain a secret")]
    private static partial void LogSecretInArguments(ILogger logger, string tool, string sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not check tool arguments for secrets; denying")]
    private static partial void LogArgumentCheckFailed(ILogger logger, Exception exception);

    /// <summary>Exposed for tests: canonical args shape the gate sees.</summary>
    internal static JsonElement? Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
