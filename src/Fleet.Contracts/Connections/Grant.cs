namespace Fleet.Contracts.Connections;

/// <summary>
/// A per-agent capability on a broker-held connection: connection × operations × constraints (FR-CON-02),
/// e.g. <c>gmail: read(label:triage), draft; no send</c>.
/// </summary>
public sealed record Grant
{
    public required string Connection { get; init; }

    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>Constraints such as labels, recipients, repos or calendars, keyed by constraint name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Constraints { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public bool Permits(string operation) => Operations.Contains(operation, StringComparer.Ordinal);
}
