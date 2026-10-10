using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Who asks for a configuration change. Only a <see cref="MessageOrigin.User"/> principal may apply proposals or run
/// lifecycle operations (FR-AGT-06, FR-AGT-07, FR-SAF-11); the gateway builds it from the authenticated caller, never
/// from message content.
/// </summary>
public sealed record ConfigPrincipal
{
    private ConfigPrincipal(MessageOrigin origin, string name)
    {
        Origin = origin;
        Name = name;
    }

    public MessageOrigin Origin { get; }

    /// <summary>Human-readable name recorded in audit entries and on decided proposals.</summary>
    public string Name { get; }

    public bool IsHuman => Origin == MessageOrigin.User;

    public static ConfigPrincipal Human(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ConfigPrincipal(MessageOrigin.User, name);
    }

    public static ConfigPrincipal Agent(AgentId agent) => new(MessageOrigin.Agent, agent.Value);

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless this is a human principal.</summary>
    internal void EnsureHuman(string action)
    {
        if (!IsHuman)
        {
            throw new UnauthorizedAccessException($"Only a human can {action}; '{Name}' is a {Origin} principal.");
        }
    }
}
