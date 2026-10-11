using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Capybara.Contracts.Messaging;

namespace Capybara.Connections.Model;

/// <summary>Which connector implementation serves a connection (FR-CON-01).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorKind>))]
public enum ConnectorKind
{
    Gmail,
    GoogleCalendar,
    GraphMail,
    GraphCalendar,
    GitHub,
}

/// <summary>The identity provider that issued a connection's credential (FR-CON-05, FR-CON-08).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CredentialProvider>))]
public enum CredentialProvider
{
    Google,
    Microsoft,
    GitHub,
}

/// <summary>
/// An account-level connection held only by the broker, with the least scopes its connector needs (FR-CON-02,
/// FR-CON-06). The id is user-chosen (for example <c>email</c> backed by Gmail) and is the first half of every broker
/// tool name <c>{connection}_{operation}</c>.
/// </summary>
public sealed partial record ConnectionDefinition
{
    private ConnectionDefinition(string id, ConnectorKind kind, CredentialProvider provider, IReadOnlyList<string> scopes)
    {
        Id = id;
        Kind = kind;
        Provider = provider;
        Scopes = scopes;
    }

    /// <summary>Lower-case letters, digits and single dashes. No underscores, so tool names split unambiguously.</summary>
    public string Id { get; }

    public ConnectorKind Kind { get; }

    public CredentialProvider Provider { get; }

    /// <summary>OAuth scopes (Google, Microsoft) or fine-grained PAT permissions (GitHub) requested at consent.</summary>
    public IReadOnlyList<string> Scopes { get; }

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <exception cref="ArgumentException">The id is not a valid connection id.</exception>
    public static ConnectionDefinition Create(string id, ConnectorKind kind)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"Connection id '{id}' must be lower-case letters, digits and single dashes.", nameof(id));
        }

        return kind switch
        {
            // gmail.compose covers drafts and send; nothing grants delete or settings access.
            ConnectorKind.Gmail => new(id, kind, CredentialProvider.Google, ["https://www.googleapis.com/auth/gmail.readonly", "https://www.googleapis.com/auth/gmail.compose"]),
            ConnectorKind.GoogleCalendar => new(id, kind, CredentialProvider.Google, ["https://www.googleapis.com/auth/calendar.events"]),

            // Drafts are created through /me/messages, which needs Mail.ReadWrite; offline_access yields a refresh token.
            ConnectorKind.GraphMail => new(id, kind, CredentialProvider.Microsoft, ["offline_access", "Mail.ReadWrite", "Mail.Send"]),
            ConnectorKind.GraphCalendar => new(id, kind, CredentialProvider.Microsoft, ["offline_access", "Calendars.ReadWrite"]),

            // Fine-grained PAT permissions, scoped to allowlisted repositories when the token is minted.
            ConnectorKind.GitHub => new(id, kind, CredentialProvider.GitHub, ["metadata:read", "issues:read", "contents:write", "pull_requests:write"]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown connector kind."),
        };
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>Human-visible connection status without secrets (FR-CON-05, FR-CON-07).</summary>
public sealed record ConnectionStatus(string Id, ConnectorKind Kind, CredentialProvider Provider, bool Authorized, DateTimeOffset? AccessTokenExpiresAt, string? Detail = null);

/// <summary>
/// A broker invocation request; raw secrets are never part of it (FR-CON-01). <paramref name="SessionId"/> is recorded
/// in the audit log only.
/// </summary>
public sealed record BrokerCall(string AgentId, string Connection, string Operation, string ArgsJson, string? SessionId = null);

/// <summary>
/// Broker invocation result redacted for model context (FR-CON-10, FR-SAF-08). <see cref="Taint"/> is
/// <see cref="Taint.Tainted"/> when the text carries third-party content; callers must apply it to the session (FR-SAF-03).
/// </summary>
public sealed record BrokerResult(bool Allowed, string Text, Taint Taint, int Size, string? Reason = null)
{
    public static BrokerResult Denied(string reason) => new(false, $"denied: {reason}", Taint.Clean, 0, reason);
}

/// <summary>
/// One broker tool an agent is granted: <c>{connection}_{operation}</c> with the connector's JSON input schema
/// (FR-CON-03). Served by <c>GET /broker/tools/{agentId}</c> and by the per-agent MCP endpoint.
/// </summary>
public sealed record BrokerToolDescriptor(string Name, string Connection, string Operation, string Description, JsonElement InputSchema, bool Untrusted)
{
    public static string ToolName(string connection, string operation) => $"{connection}_{operation}";
}
