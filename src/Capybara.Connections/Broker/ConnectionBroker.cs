using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Connections.OAuth;
using Capybara.Contracts.Diagnostics;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Audit;
using Capybara.Policy.Canonicalization;
using Capybara.Policy.Redaction;
using Microsoft.Extensions.Logging;

namespace Capybara.Connections.Broker;

/// <summary>
/// The single broker entry point (FR-CON-01, FR-CON-10): freeze check, default-deny grants, broker-held credentials,
/// redaction of the result, and one audit record per call with agent, tool, args hash, decision, result size and
/// taint.
/// </summary>
/// <remarks>
/// <para>
/// Decide phase (freeze, grant, credential): every outcome is audited as one <c>broker_call</c> record; failures
/// become a denial naming the stage and the exception type (never its message, which may echo provider data). If the
/// audit log itself fails here the exception propagates, since nothing has been sent.
/// </para>
/// <para>
/// Act phase: the call registers as in flight with <see cref="BrokerFreeze"/> and re-checks the freeze, then writes a
/// <c>broker_attempt</c> record (same args hash and trace id) before contacting the provider; if that write fails the
/// call is denied unsent (fail closed). The outcome is then audited as <c>broker_call</c>. Connector failures after the
/// request may have left are reported as "outcome unknown". If the outcome write fails, the real result is returned
/// with a note, never a false denial. Caller cancellation is audited and rethrown.
/// </para>
/// </remarks>
public sealed class ConnectionBroker(GrantEnforcer grants, BrokerToolCatalog catalog, ConnectorRegistry connectors, CredentialManager credentials, IAuditLog audit, BrokerFreeze freeze, ILogger<ConnectionBroker> logger)
{
    public async ValueTask<BrokerResult> InvokeAsync(BrokerCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await TraceAsync(call.AgentId, BrokerToolDescriptor.ToolName(call.Connection, call.Operation), () => DecideAndInvokeAsync(call, cancellationToken));
    }

    /// <summary>
    /// Resolves a tool name (as an MCP client sends it) against the agent's grants and invokes it. A name the agent is
    /// not granted is denied and audited under that name (FR-CON-10).
    /// </summary>
    public async ValueTask<BrokerResult> InvokeToolAsync(string agentId, string toolName, string argsJson, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        toolName ??= string.Empty;
        if (catalog.Find(agentId, toolName) is { } tool)
        {
            return await InvokeAsync(new BrokerCall(agentId, tool.Connection, tool.Operation, argsJson, sessionId), cancellationToken);
        }

        return await TraceAsync(agentId, toolName, async () =>
        {
            using var document = TryParse(argsJson, out var argsHash);
            return await DenyAsync(agentId, sessionId, toolName, argsHash, "tool is not granted");
        });
    }

    private static async ValueTask<BrokerResult> TraceAsync(string agentId, string toolName, Func<ValueTask<BrokerResult>> body)
    {
        using var activity = CapybaraTelemetry.Connections.StartActivity("broker.invoke");
        activity?.SetTag("capybara.agent", agentId);
        activity?.SetTag("capybara.tool", toolName);
        var result = await body();
        activity?.SetTag("capybara.decision", result.Allowed ? "allow" : "deny");
        if (!result.Allowed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);
        }

        return result;
    }

    private async ValueTask<BrokerResult> DecideAndInvokeAsync(BrokerCall call, CancellationToken cancellationToken)
    {
        var tool = BrokerToolDescriptor.ToolName(call.Connection, call.Operation);
        using var document = TryParse(call.ArgsJson, out var argsHash);
        if (document is null)
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, "arguments are not valid JSON");
        }

        // Phase 1: decide. Nothing has reached the provider, so any failure (including of the audit log) is a denial.
        var args = document.RootElement;
        IConnector connector;
        string accessToken;
        var stage = "grant check";
        try
        {
            if (freeze.IsFrozen)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"broker frozen ({freeze.Reason})");
            }

            var grant = grants.Decide(call.AgentId, call.Connection, call.Operation, args);
            if (!grant.Allowed)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, grant.Reason);
            }

            if (connectors.Find(call.Connection) is not { } found)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, "unknown connection");
            }

            stage = "credential";
            var token = await credentials.GetAccessTokenAsync(found.Connection, cancellationToken);
            if (token.AccessToken is null)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, token.Problem ?? "no credential");
            }

            (connector, accessToken) = (found, token.AccessToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditAsync(call.AgentId, call.SessionId, tool, argsHash, "deny", $"cancelled by caller during {stage}", 0, Taint.Clean);
            throw;
        }
        catch (Exception ex)
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"{stage} failed ({ex.GetType().Name})");
        }

        // Phase 2: act. Registering as in flight and re-checking the freeze closes the window in which a freeze lands
        // during the credential refresh (FR-SAF-06).
        if (!freeze.TryEnterCall())
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"broker frozen ({freeze.Reason})");
        }

        try
        {
            // The attempt is on record before the provider is contacted; if it cannot be recorded, nothing is sent.
            try
            {
                await audit.AppendAsync(Record("broker_attempt", "attempt", call, tool, argsHash, "invoking connector; size=0; taint=Clean"), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Broker audit log unavailable; {Tool} for {Agent} was not attempted.", tool, call.AgentId);
                return BrokerResult.Denied("audit log unavailable; the call was not attempted");
            }

            BrokerResult outcome;
            try
            {
                var result = await connector.InvokeAsync(call.Operation, args, accessToken, cancellationToken);

                // The access token is redacted verbatim, then common token shapes by pattern (FR-SAF-08).
                var text = new SecretRedactor([accessToken]).Redact(result.Text);
                outcome = result with { Text = text, Size = Encoding.UTF8.GetByteCount(text) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecordOutcomeAsync(call, tool, argsHash, BrokerResult.Denied("cancelled by caller during connector; outcome unknown"));
                throw;
            }
            catch (ArgumentException ex)
            {
                outcome = BrokerResult.Denied($"invalid arguments: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                outcome = BrokerResult.Denied("connector timed out; outcome unknown");
            }
            catch (HttpRequestException ex)
            {
                outcome = BrokerResult.Denied($"connector unreachable ({ex.HttpRequestError}); outcome unknown");
            }
            catch (Exception ex)
            {
                outcome = BrokerResult.Denied($"connector failed ({ex.GetType().Name}); outcome unknown");
            }

            return await RecordOutcomeAsync(call, tool, argsHash, outcome);
        }
        finally
        {
            freeze.ExitCall();
        }
    }

    /// <summary>
    /// Audits the outcome of a call that reached the connector. If that write fails the real outcome is still returned,
    /// flagged in <see cref="BrokerResult.Reason"/>: reporting a completed action as denied invites a duplicate retry.
    /// The preceding attempt record shows the call happened.
    /// </summary>
    private async ValueTask<BrokerResult> RecordOutcomeAsync(BrokerCall call, string tool, string argsHash, BrokerResult outcome)
    {
        try
        {
            await audit.AppendAsync(Record("broker_call", outcome.Allowed ? "allow" : "deny", call, tool, argsHash, $"{outcome.Reason ?? "ok"}; size={outcome.Size}; taint={outcome.Taint}"), CancellationToken.None);
            return outcome;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Broker outcome audit failed after {Tool} for {Agent} reached the provider (allowed={Allowed}).", tool, call.AgentId, outcome.Allowed);
            const string note = "outcome audit failed";
            return outcome with { Reason = outcome.Allowed ? $"action completed but {note}" : $"{outcome.Reason}; {note}" };
        }
    }
    /// <summary>Parses arguments; on failure returns <see langword="null"/> and hashes the raw text instead.</summary>
    private static JsonDocument? TryParse(string? argsJson, out string argsHash)
    {
        try
        {
            var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            argsHash = CanonicalJson.Hash(document.RootElement);
            return document;
        }
        catch (JsonException)
        {
            argsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(argsJson ?? string.Empty)));
            return null;
        }
    }

    private async ValueTask<BrokerResult> DenyAsync(string agentId, string? sessionId, string tool, string argsHash, string reason)
    {
        await AuditAsync(agentId, sessionId, tool, argsHash, "deny", reason, 0, Taint.Clean);
        return BrokerResult.Denied(reason);
    }

    /// <summary>Never cancelled: an action that happened, or was refused, must be on record.</summary>
    private ValueTask<AuditEntry> AuditAsync(string agentId, string? sessionId, string tool, string argsHash, string decision, string detail, int size, Taint taint) =>
        audit.AppendAsync(
            new AuditRecord("broker_call", decision, agentId, sessionId, tool, argsHash, $"{detail}; size={size}; taint={taint}", Activity.Current?.TraceId.ToString()),
            CancellationToken.None);

    private static AuditRecord Record(string kind, string decision, BrokerCall call, string tool, string argsHash, string detail) =>
        new(kind, decision, call.AgentId, call.SessionId, tool, argsHash, detail, Activity.Current?.TraceId.ToString());
}
