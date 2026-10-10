using Capybara.Contracts.Messaging;

namespace Capybara.Connections.Model;

/// <summary>External service kind held by the broker (FR-CON-01).</summary>
public enum ConnectionProviderKind
{
    Gmail,
    MicrosoftGraph,
    GitHub,
    Browser,
    Mcp,
}

/// <summary>Account-level connection definition with least scopes (FR-CON-06).</summary>
public sealed record ConnectionDefinition
{
    public required string Id { get; init; }

    public required ConnectionProviderKind Provider { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public static ConnectionDefinition Gmail(string id) => new()
    {
        Id = id,
        Provider = ConnectionProviderKind.Gmail,
        Scopes = ["https://www.googleapis.com/auth/gmail.readonly", "https://www.googleapis.com/auth/gmail.compose"],
    };

    public static ConnectionDefinition MicrosoftMail(string id) => new()
    {
        Id = id,
        Provider = ConnectionProviderKind.MicrosoftGraph,
        Scopes = ["Mail.Read", "Mail.Send", "Calendars.Read"],
    };

    public static ConnectionDefinition GitHub(string id) => new()
    {
        Id = id,
        Provider = ConnectionProviderKind.GitHub,
        Scopes = ["fine-grained:contents:read", "fine-grained:pull_requests:write", "fine-grained:issues:read"],
    };
}

/// <summary>Human-visible connection status without secrets (FR-CON-07).</summary>
public sealed record ConnectionStatus(string Id, ConnectionProviderKind Provider, bool Healthy, DateTimeOffset? ExpiresAt, string? Detail = null);

/// <summary>A broker invocation request; raw secrets are never part of it (FR-CON-01).</summary>
public sealed record BrokerCall(string AgentId, string Connection, string Operation, string ArgsJson);

/// <summary>Broker invocation result redacted for model context (FR-CON-10, FR-SAF-08).</summary>
public sealed record BrokerResult(bool Allowed, string Text, Taint Taint, int Size, string? Reason = null);
