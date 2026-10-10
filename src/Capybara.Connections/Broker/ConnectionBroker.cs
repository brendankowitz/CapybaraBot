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
/// taint. Every failure is a denied <see cref="BrokerResult"/>, never an unaudited exception.
/// </summary>
public sealed class ConnectionBroker(GrantEnforcer grants, ConnectorRegistry connectors, CredentialManager credentials, IAuditLog audit, BrokerFreeze freeze)
{
    public async ValueTask<BrokerResult> InvokeAsync(BrokerCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var activity = CapybaraTelemetry.Connections.StartActivity("broker.invoke");
        activity?.SetTag("capybara.agent", call.AgentId);
        activity?.SetTag("capybara.tool", BrokerToolDescriptor.ToolName(call.Connection, call.Operation));

        var result = await DecideAndInvokeAsync(call, cancellationToken);
        activity?.SetTag("capybara.decision", result.Allowed ? "allow" : "deny");
        if (!result.Allowed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Reason);
        }

        return result;
    }

    private async ValueTask<BrokerResult> DecideAndInvokeAsync(BrokerCall call, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgsJson) ? "{}" : call.ArgsJson);
        }
        catch (JsonException)
        {
            var rawHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgsJson)));
            return await DenyAsync(call, rawHash, "arguments are not valid JSON", cancellationToken);
        }

        using (document)
        {
            var args = document.RootElement;
            var argsHash = CanonicalJson.Hash(args);
            if (freeze.IsFrozen)
            {
                return await DenyAsync(call, argsHash, $"broker frozen ({freeze.Reason})", cancellationToken);
            }

            var grant = grants.Decide(call.AgentId, call.Connection, call.Operation, args);
            if (!grant.Allowed)
            {
                return await DenyAsync(call, argsHash, grant.Reason, cancellationToken);
            }

            if (connectors.Find(call.Connection) is not { } connector)
            {
                return await DenyAsync(call, argsHash, "unknown connection", cancellationToken);
            }

            var token = await credentials.GetAccessTokenAsync(connector.Connection, cancellationToken);
            if (token.AccessToken is null)
            {
                return await DenyAsync(call, argsHash, token.Problem ?? "no credential", cancellationToken);
            }

            BrokerResult result;
            try
            {
                result = await connector.InvokeAsync(call.Operation, args, token.AccessToken, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return await DenyAsync(call, argsHash, $"invalid arguments: {ex.Message}", cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return await DenyAsync(call, argsHash, $"connector unreachable: {ex.HttpRequestError}", cancellationToken);
            }

            // The access token is redacted verbatim, then common token shapes by pattern (FR-SAF-08).
            var text = new SecretRedactor([token.AccessToken]).Redact(result.Text);
            var redacted = result with { Text = text, Size = Encoding.UTF8.GetByteCount(text) };
            await AuditAsync(call, argsHash, redacted.Allowed ? "allow" : "deny", redacted.Reason ?? "ok", redacted.Size, redacted.Taint, cancellationToken);
            return redacted;
        }
    }

    private async ValueTask<BrokerResult> DenyAsync(BrokerCall call, string argsHash, string reason, CancellationToken cancellationToken)
    {
        await AuditAsync(call, argsHash, "deny", reason, 0, Taint.Clean, cancellationToken);
        return BrokerResult.Denied(reason);
    }

    private ValueTask<AuditEntry> AuditAsync(BrokerCall call, string argsHash, string decision, string detail, int size, Taint taint, CancellationToken cancellationToken) =>
        audit.AppendAsync(
            new AuditRecord(
                "broker_call",
                decision,
                Agent: call.AgentId,
                SessionId: call.SessionId,
                Tool: BrokerToolDescriptor.ToolName(call.Connection, call.Operation),
                ArgsHash: argsHash,
                Detail: $"{detail}; size={size}; taint={taint}",
                TraceId: Activity.Current?.TraceId.ToString()),
            cancellationToken);
}
