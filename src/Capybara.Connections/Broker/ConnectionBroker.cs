using System.Text;
using System.Text.Json;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Connections.Vault;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Audit;
using Capybara.Policy.Canonicalization;
using Capybara.Policy.Safety;

namespace Capybara.Connections.Broker;

/// <summary>Single broker entry point enforcing kill switch, grants, vault access, redaction and audit (FR-CON-01, FR-CON-10).</summary>
public sealed class ConnectionBroker(GrantEnforcer grants, TokenVault vault, IConnectorRegistry connectors, IAuditLog audit, KillSwitch killSwitch, SecretRedactor redactor)
{
    public async ValueTask<BrokerResult> InvokeAsync(string agentId, string connection, string operation, string argsJson, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var argsHash = CanonicalJson.Hash(document.RootElement);
        if (killSwitch.IsEngaged)
        {
            await AuditAsync(agentId, operation, argsHash, "deny", "kill switch engaged", 0, Taint.Clean, cancellationToken);
            return new BrokerResult(false, "denied: kill switch engaged", Taint.Clean, 0, "kill switch engaged");
        }

        var grant = grants.Decide(agentId, connection, operation, document.RootElement);
        if (!grant.Allowed)
        {
            await AuditAsync(agentId, operation, argsHash, "deny", grant.Reason, 0, Taint.Clean, cancellationToken);
            return new BrokerResult(false, $"denied: {grant.Reason}", Taint.Clean, 0, grant.Reason);
        }

        var connector = connectors.Find(connection);
        if (connector is null)
        {
            await AuditAsync(agentId, operation, argsHash, "deny", "unknown connection", 0, Taint.Clean, cancellationToken);
            return new BrokerResult(false, "denied: unknown connection", Taint.Clean, 0, "unknown connection");
        }

        var token = await vault.GetTokenAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            await AuditAsync(agentId, operation, argsHash, "deny", "missing token", 0, Taint.Clean, cancellationToken);
            return new BrokerResult(false, "denied: missing token", Taint.Clean, 0, "missing token");
        }

        var knownRedactor = new SecretRedactor([token]);
        var result = await connector.InvokeAsync(operation, document.RootElement, token, cancellationToken);
        var text = redactor.Redact(knownRedactor.Redact(result.Text));
        var size = Encoding.UTF8.GetByteCount(text);
        var redacted = result with { Text = text, Size = size };
        await AuditAsync(agentId, operation, argsHash, result.Allowed ? "allow" : "deny", result.Reason ?? "ok", size, redacted.Taint, cancellationToken);
        return redacted;
    }

    private async ValueTask AuditAsync(string agentId, string operation, string argsHash, string decision, string detail, int resultSize, Taint taint, CancellationToken cancellationToken) =>
        await audit.AppendAsync(new AuditRecord("broker_call", decision, Agent: agentId, Tool: operation, ArgsHash: argsHash, Detail: $"{detail}; size={resultSize}; taint={taint}"), cancellationToken);
}
