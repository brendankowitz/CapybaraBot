using System.Text.Json;
using Capybara.Contracts.Connections;
using Capybara.Policy.Rules;

namespace Capybara.Connections.Grants;

/// <summary>Resolves per-agent connection grants (FR-CON-02).</summary>
public interface IGrantProvider
{
    IReadOnlyList<Grant> GetGrants(string agentId);
}

/// <summary>Mutable in-memory grant provider for tests and bootstrap config; human UI owns mutations (FR-CON-07).</summary>
public sealed class InMemoryGrantProvider : IGrantProvider
{
    private readonly Dictionary<string, IReadOnlyList<Grant>> _grants = new(StringComparer.Ordinal);

    public InMemoryGrantProvider Set(string agentId, IReadOnlyList<Grant> grants)
    {
        _grants[agentId] = grants;
        return this;
    }

    public IReadOnlyList<Grant> GetGrants(string agentId) => _grants.GetValueOrDefault(agentId, []);
}

/// <summary>Connection × operation × constraints enforcement with default deny (FR-CON-02).</summary>
public sealed class GrantEnforcer(IGrantProvider grants)
{
    public GrantDecision Decide(string agentId, string connection, string operation, JsonElement args)
    {
        var grant = grants.GetGrants(agentId).FirstOrDefault(g => string.Equals(g.Connection, connection, StringComparison.Ordinal) && g.Permits(operation));
        if (grant is null)
        {
            return GrantDecision.Deny("no matching grant");
        }

        foreach (var constraint in grant.Constraints)
        {
            if (!AllowsConstraint(args, constraint.Key, constraint.Value))
            {
                return GrantDecision.Deny($"constraint denied: {constraint.Key}");
            }
        }

        return GrantDecision.Allow();
    }

    private static bool AllowsConstraint(JsonElement args, string name, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0)
        {
            return false;
        }

        if (!args.TryGetProperty(name, out var value))
        {
            return false;
        }

        var actuals = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(ReadString).Where(static v => v is not null).Select(static v => v!).ToArray()
            : [ReadString(value) ?? string.Empty];

        return actuals.Length > 0 && actuals.All(actual => allowed.Any(pattern => Glob.IsMatch(pattern, actual)));
    }

    private static string? ReadString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
}

/// <summary>Grant decision and denial reason (FR-CON-02).</summary>
public sealed record GrantDecision(bool Allowed, string Reason)
{
    public static GrantDecision Allow() => new(true, "allowed");

    public static GrantDecision Deny(string reason) => new(false, reason);
}

/// <summary>Human-only administration surface for connections and grants (FR-CON-07, FR-SAF-11).</summary>
public sealed class ConnectionAdministration(InMemoryGrantProvider grants)
{
    public void ReplaceGrants(HumanPrincipal principal, string agentId, IReadOnlyList<Grant> newGrants)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Surface is HumanSurface.Agent)
        {
            throw new InvalidOperationException("Agents cannot administer connections or grants.");
        }

        grants.Set(agentId, newGrants);
    }
}

/// <summary>Explicit human principal required for connection administration (FR-CON-07).</summary>
public sealed record HumanPrincipal(string Subject, HumanSurface Surface);

public enum HumanSurface
{
    Phone,
    Desktop,
    Tui,
    Agent,
}
