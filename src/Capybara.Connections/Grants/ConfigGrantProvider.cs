using System.Collections.Concurrent;
using Capybara.Contracts.Connections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Capybara.Connections.Grants;

/// <summary>
/// Read-only grants loaded by the broker itself from <c>{ConfigRoot}/agents/*/agent.yaml</c> (FR-CON-02). The broker
/// reads the user-owned config directly rather than trusting capybarad to tell it what an agent may do, and nothing
/// can add or widen grants at runtime: editing them is a human config change followed by a broker restart (FR-CON-07).
/// The only runtime change is <see cref="Revoke"/>, which can only take privilege away (FR-AGT-07).
/// </summary>
public sealed class ConfigGrantProvider : IGrantProvider
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ConcurrentDictionary<string, IReadOnlyList<Grant>> _grants;

    private ConfigGrantProvider(Dictionary<string, IReadOnlyList<Grant>> grants) => _grants = new(grants, StringComparer.Ordinal);

    /// <summary>Grants per agent id, including agents with none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Grant>> All => _grants;

    public IReadOnlyList<Grant> GetGrants(string agentId) => _grants.GetValueOrDefault(agentId, []);

    /// <summary>
    /// Drops every grant of an agent that was archived or deleted, until a restart reloads the config (where the agent
    /// is gone). Returns whether the agent had an entry. Idempotent.
    /// </summary>
    public bool Revoke(string agentId) => _grants.TryRemove(agentId, out _);

    /// <summary>Loads every agent's grants. Fails closed: any unreadable or malformed file aborts the load.</summary>
    /// <exception cref="GrantConfigurationException">With every error found.</exception>
    public static ConfigGrantProvider Load(string configRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        var errors = new List<string>();
        var grants = new Dictionary<string, IReadOnlyList<Grant>>(StringComparer.Ordinal);
        var agentsDir = Path.Combine(configRoot, "agents");
        if (!Directory.Exists(agentsDir))
        {
            throw new GrantConfigurationException([$"missing agents directory '{agentsDir}'."]);
        }

        foreach (var dir in Directory.EnumerateDirectories(agentsDir).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.') || folder.StartsWith('_'))
            {
                continue;
            }

            var file = Path.Combine(dir, "agent.yaml");
            if (!File.Exists(file))
            {
                errors.Add($"{folder}: missing agent.yaml.");
                continue;
            }

            AgentGrantsYaml? yaml;
            try
            {
                yaml = Yaml.Deserialize<AgentGrantsYaml?>(File.ReadAllText(file));
            }
            catch (YamlException ex)
            {
                errors.Add($"{folder}/agent.yaml: {ex.Message}");
                continue;
            }

            if (yaml is null || !string.Equals(yaml.Id, folder, StringComparison.Ordinal))
            {
                errors.Add($"{folder}: agent.yaml id must match its folder name.");
                continue;
            }

            var agentGrants = new List<Grant>();
            foreach (var grant in yaml.Grants)
            {
                if (string.IsNullOrWhiteSpace(grant.Connection) || grant.Operations.Count == 0)
                {
                    errors.Add($"{folder}: every grant needs a connection and at least one operation.");
                    continue;
                }

                agentGrants.Add(new Grant
                {
                    Connection = grant.Connection,
                    Operations = [.. grant.Operations],
                    Constraints = grant.Constraints.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.Ordinal),
                });
            }

            grants[folder] = agentGrants;
        }

        return errors.Count > 0 ? throw new GrantConfigurationException(errors) : new ConfigGrantProvider(grants);
    }

    private sealed class AgentGrantsYaml
    {
        public string? Id { get; set; }

        public List<GrantYaml> Grants { get; set; } = [];
    }

    private sealed class GrantYaml
    {
        public string? Connection { get; set; }

        public List<string> Operations { get; set; } = [];

        public Dictionary<string, List<string>> Constraints { get; set; } = [];
    }
}

/// <summary>The broker's grant or connection configuration is invalid; the broker refuses to start (fail closed).</summary>
public sealed class GrantConfigurationException : Exception
{
    public GrantConfigurationException()
    {
    }

    public GrantConfigurationException(string message)
        : base(message)
    {
    }

    public GrantConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GrantConfigurationException(IReadOnlyList<string> errors)
        : base("Invalid broker configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; } = [];
}
