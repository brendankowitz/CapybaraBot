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

namespace Capybara.Connections.Broker;

/// <summary>
/// The single broker entry point (FR-CON-01, FR-CON-10): freeze check, default-deny grants, broker-held credentials,
/// redaction of the result, and one audit record per call with agent, tool, args hash, decision, result size and
/// taint.
/// </summary>
/// <remarks>
/// Every outcome is audited. Failures become a denied <see cref="BrokerResult"/> naming the failing stage and the
/// exception type (never its message, which may echo provider data). A connector timeout is audited as
/// "outcome unknown", because the provider may already have acted. Caller cancellation is audited the same way and
/// then rethrown. Only a failure of the audit log itself propagates.
/// </remarks>
public sealed class ConnectionBroker(GrantEnforcer grants, BrokerToolCatalog catalog, ConnectorRegistry connectors, CredentialManager credentials, IAuditLog audit, BrokerFreeze freeze)
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

        var stage = "grant check";
        try
        {
            if (freeze.IsFrozen)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"broker frozen ({freeze.Reason})");
            }

            var args = document.RootElement;
            var grant = grants.Decide(call.AgentId, call.Connection, call.Operation, args);
            if (!grant.Allowed)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, grant.Reason);
            }

            if (connectors.Find(call.Connection) is not { } connector)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, "unknown connection");
            }

            stage = "credential";
            var token = await credentials.GetAccessTokenAsync(connector.Connection, cancellationToken);
            if (token.AccessToken is null)
            {
                return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, token.Problem ?? "no credential");
            }

            stage = "connector";
            var result = await connector.InvokeAsync(call.Operation, args, token.AccessToken, cancellationToken);

            // The access token is redacted verbatim, then common token shapes by pattern (FR-SAF-08).
            var text = new SecretRedactor([token.AccessToken]).Redact(result.Text);
            var redacted = result with { Text = text, Size = Encoding.UTF8.GetByteCount(text) };
            await AuditAsync(call.AgentId, call.SessionId, tool, argsHash, redacted.Allowed ? "allow" : "deny", redacted.Reason ?? "ok", redacted.Size, redacted.Taint);
            return redacted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var outcome = stage == "connector" ? "; outcome unknown" : string.Empty;
            await AuditAsync(call.AgentId, call.SessionId, tool, argsHash, "deny", $"cancelled by caller during {stage}{outcome}", 0, Taint.Clean);
            throw;
        }
        catch (ArgumentException ex) when (stage == "connector")
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"invalid arguments: {ex.Message}");
        }
        catch (OperationCanceledException) when (stage == "connector")
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, "connector timed out; outcome unknown");
        }
        catch (HttpRequestException ex) when (stage == "connector")
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"connector unreachable ({ex.HttpRequestError}); outcome unknown");
        }
        catch (Exception ex)
        {
            return await DenyAsync(call.AgentId, call.SessionId, tool, argsHash, $"{stage} failed ({ex.GetType().Name})");
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
            new AuditRecord(
                "broker_call",
                decision,
                Agent: agentId,
                SessionId: sessionId,
                Tool: tool,
                ArgsHash: argsHash,
                Detail: $"{detail}; size={size}; taint={taint}",
                TraceId: Activity.Current?.TraceId.ToString()),
            CancellationToken.None);
}
