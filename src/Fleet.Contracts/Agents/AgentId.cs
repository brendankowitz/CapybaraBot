using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Fleet.Contracts.Agents;

/// <summary>
/// A stable logical agent identifier (FR-AGT-01). Lower-case, 2–32 characters, never PII or a conversation id.
/// </summary>
public readonly partial record struct AgentId
{
    private AgentId(string value) => Value = value;

    public string Value { get; }

    public static AgentId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"'{value}' is not a valid agent id. Expected ^[a-z][a-z0-9-]{{1,31}}$.");

    public static bool TryParse([NotNullWhen(true)] string? value, out AgentId id)
    {
        if (value is not null && Pattern().IsMatch(value))
        {
            id = new AgentId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;

    public static implicit operator string(AgentId id) => id.Value;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
