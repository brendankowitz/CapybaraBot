using Capybara.Connections.Model;

namespace Capybara.Connections.Connectors;

/// <summary>The broker's configured connections, immutable after startup (FR-CON-01).</summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors;

    /// <exception cref="ArgumentException">Two connectors share a connection id.</exception>
    public ConnectorRegistry(IEnumerable<IConnector> connectors)
    {
        _connectors = new Dictionary<string, IConnector>(StringComparer.Ordinal);
        foreach (var connector in connectors)
        {
            if (!_connectors.TryAdd(connector.Connection.Id, connector))
            {
                throw new ArgumentException($"Connection '{connector.Connection.Id}' is configured twice.", nameof(connectors));
            }
        }
    }

    public IConnector? Find(string connectionId) => _connectors.GetValueOrDefault(connectionId);

    public IReadOnlyList<IConnector> List() => [.. _connectors.Values.OrderBy(c => c.Connection.Id, StringComparer.Ordinal)];

    /// <summary>Creates the connector for a connection definition over a provider-specific HTTP client.</summary>
    public static IConnector Create(ConnectionDefinition connection, HttpClient http) => connection.Kind switch
    {
        ConnectorKind.Gmail => new GmailConnector(http, connection),
        ConnectorKind.GoogleCalendar => new GoogleCalendarConnector(http, connection),
        ConnectorKind.GraphMail => new GraphMailConnector(http, connection),
        ConnectorKind.GraphCalendar => new GraphCalendarConnector(http, connection),
        ConnectorKind.GitHub => new GitHubConnector(http, connection),
        _ => throw new ArgumentOutOfRangeException(nameof(connection), connection.Kind, "Unknown connector kind."),
    };
}
