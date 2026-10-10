using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Contracts.Connections;

namespace Capybara.Connections.Broker;

/// <summary>
/// The one naming contract between config, broker and sessions (FR-CON-02, FR-CON-03): a grant of operation
/// <c>op</c> on connection <c>conn</c> yields the tool <c>{conn}_{op}</c>, where <c>op</c> is a connector operation
/// name. Agents list these names in their tool allowlist.
/// </summary>
public sealed class BrokerToolCatalog(IGrantProvider grants, ConnectorRegistry connectors)
{
    /// <summary>Granted tools that a configured connector actually serves. Default deny: no grant, no tool.</summary>
    public IReadOnlyList<BrokerToolDescriptor> ForAgent(string agentId)
    {
        var tools = new Dictionary<string, BrokerToolDescriptor>(StringComparer.Ordinal);
        foreach (var grant in grants.GetGrants(agentId))
        {
            if (connectors.Find(grant.Connection) is not { } connector)
            {
                continue;
            }

            foreach (var operation in connector.Operations.Where(o => grant.Permits(o.Name)))
            {
                var name = BrokerToolDescriptor.ToolName(connector.Connection.Id, operation.Name);
                tools.TryAdd(name, new BrokerToolDescriptor(name, connector.Connection.Id, operation.Name, operation.Description, operation.InputSchema, operation.Untrusted));
            }
        }

        return [.. tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    /// <summary>Resolves a tool name without parsing it, so connection ids and operations may both contain separators.</summary>
    public BrokerToolDescriptor? Find(string agentId, string toolName) =>
        ForAgent(agentId).FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));

    /// <summary>
    /// Startup check that every grant names a configured connection, real operations and constraints the operations
    /// actually take. A constraint on a missing argument would deny every call, which hides naming drift.
    /// </summary>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, IReadOnlyList<Grant>> grantsByAgent)
    {
        ArgumentNullException.ThrowIfNull(grantsByAgent);
        var errors = new List<string>();
        foreach (var (agent, agentGrants) in grantsByAgent)
        {
            foreach (var grant in agentGrants)
            {
                if (connectors.Find(grant.Connection) is not { } connector)
                {
                    errors.Add($"{agent}: grant names connection '{grant.Connection}', which the broker does not configure.");
                    continue;
                }

                foreach (var operation in grant.Operations)
                {
                    var descriptor = connector.Operations.FirstOrDefault(o => string.Equals(o.Name, operation, StringComparison.Ordinal));
                    if (descriptor is null)
                    {
                        errors.Add($"{agent}: '{grant.Connection}' has no operation '{operation}' (available: {string.Join(", ", connector.Operations.Select(o => o.Name))}).");
                        continue;
                    }

                    var arguments = descriptor.InputSchema.GetProperty("properties");
                    foreach (var constraint in grant.Constraints.Keys.Where(k => !arguments.TryGetProperty(k, out _)))
                    {
                        errors.Add($"{agent}: constraint '{constraint}' is not an argument of {grant.Connection} {operation}, so the grant would deny every call.");
                    }
                }
            }
        }

        return errors;
    }
}
