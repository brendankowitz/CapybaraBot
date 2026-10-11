using System.Runtime.Versioning;
using Capybara.Connections.Broker;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Model;
using Capybara.Contracts.Connections;
using Capybara.Testing;
using YamlDotNet.RepresentationModel;

namespace Capybara.Connections.Tests;

public sealed class ConfigGrantProviderTests
{
    /// <summary>The broker connections the sample config assumes (src/Capybara.Broker/appsettings.json).</summary>
    private static readonly ConnectorRegistry SampleConnections = new(
    [
        ConnectorRegistry.Create(ConnectionDefinition.Create("email", ConnectorKind.Gmail), new HttpClient()),
        ConnectorRegistry.Create(ConnectionDefinition.Create("calendar", ConnectorKind.GoogleCalendar), new HttpClient()),
        ConnectorRegistry.Create(ConnectionDefinition.Create("github", ConnectorKind.GitHub), new HttpClient()),
    ]);

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void An_agent_folder_the_broker_cannot_read_fails_startup_with_a_clear_message()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are not supported on Windows.");
        Assert.SkipWhen(Environment.UserName == "root", "root reads everything regardless of mode.");
        using var dir = new TempDirectory();
        var agent = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agent, "agent.yaml"), "id: ea\n");
        File.SetUnixFileMode(Path.Combine(agent, "agent.yaml"), UnixFileMode.None);
        try
        {
            var ex = Assert.Throws<GrantConfigurationException>(() => ConfigGrantProvider.Load(dir.Path));

            Assert.Contains("ea/agent.yaml cannot be read", ex.Message, StringComparison.Ordinal);
            Assert.Contains("group 'capybara'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(agent, "agent.yaml"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Maps_sample_agent_yaml_grants()
    {
        var provider = ConfigGrantProvider.Load(RepositoryPaths.SampleConfig);

        Assert.Equal(["code", "ea", "research"], provider.All.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(provider.GetGrants("research"));
        var email = Assert.Single(provider.GetGrants("ea"), g => g.Connection == "email");
        Assert.Equal(["list", "read", "draft", "send"], email.Operations);
        var write = Assert.Single(provider.GetGrants("code"), g => g.Permits("create_pr"));
        Assert.Equal(["agent/*"], write.Constraints["branch"]);
        Assert.Empty(provider.GetGrants("unknown-agent"));

        // Unapproved writes are only safe on a curated list of repos, never an owner-wide glob.
        Assert.All(provider.All.Values.SelectMany(g => g).Where(g => g.Constraints.ContainsKey("repo")).SelectMany(g => g.Constraints["repo"]), repo => Assert.DoesNotContain('*', repo));
    }

    [Fact]
    public void Sample_grants_name_real_connector_operations_and_arguments()
    {
        var provider = ConfigGrantProvider.Load(RepositoryPaths.SampleConfig);

        var errors = new BrokerToolCatalog(provider, SampleConnections).Validate(provider.All);

        Assert.Empty(errors);
    }

    /// <summary>
    /// The naming contract end to end: every broker-shaped name in an agent's tool allowlist is a tool the broker
    /// grants that agent, and every granted tool is allowlisted, so the FR-AGT-09 self-test can pass.
    /// </summary>
    [Fact]
    public void Sample_tool_allowlists_match_granted_broker_tools()
    {
        var provider = ConfigGrantProvider.Load(RepositoryPaths.SampleConfig);
        var catalog = new BrokerToolCatalog(provider, SampleConnections);
        var connectionPrefixes = SampleConnections.List().Select(c => c.Connection.Id + "_").ToArray();

        foreach (var agent in provider.All.Keys)
        {
            var allowlisted = ReadTools(agent).Where(t => connectionPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal))).Order(StringComparer.Ordinal);
            var granted = catalog.ForAgent(agent).Select(t => t.Name).Order(StringComparer.Ordinal);

            Assert.Equal(granted, allowlisted);
        }

        Assert.Contains(catalog.ForAgent("ea"), t => t.Name == "email_send");
        Assert.Empty(catalog.ForAgent("research"));
    }

    [Fact]
    public void Validation_reports_unknown_operations_connections_and_dead_constraints()
    {
        var grants = new Dictionary<string, IReadOnlyList<Grant>>
        {
            ["ea"] =
            [
                new Grant { Connection = "email", Operations = ["read", "write"] },
                new Grant { Connection = "gmail", Operations = ["read"] },
                new Grant { Connection = "github", Operations = ["list_issues"], Constraints = new Dictionary<string, IReadOnlyList<string>> { ["branch"] = ["agent/*"] } },
            ],
        };

        var errors = new BrokerToolCatalog(new InMemoryGrantProvider(), SampleConnections).Validate(grants);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("no operation 'write'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'gmail'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("constraint 'branch'", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_config_fails_closed_with_every_error()
    {
        using var dir = new TempDirectory();
        Write(dir.Path, "ea", "id: not-ea\n");
        Write(dir.Path, "code", "id: code\ngrants:\n  - operations: [read]\n");
        Write(dir.Path, "broken", "id: broken\ngrants: [\n");

        var ex = Assert.Throws<GrantConfigurationException>(() => ConfigGrantProvider.Load(dir.Path));

        Assert.Equal(3, ex.Errors.Count);
    }

    [Fact]
    public void Missing_agents_directory_fails_closed()
    {
        using var dir = new TempDirectory();

        Assert.Throws<GrantConfigurationException>(() => ConfigGrantProvider.Load(dir.Path));
    }

    private static void Write(string root, string agent, string yaml)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "agents", agent)).FullName;
        File.WriteAllText(Path.Combine(folder, "agent.yaml"), yaml);
    }

    private static IEnumerable<string> ReadTools(string agent)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(RepositoryPaths.SampleConfig, "agents", agent, "agent.yaml"));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return ((YamlSequenceNode)root.Children[new YamlScalarNode("tools")]).Children.Select(n => ((YamlScalarNode)n).Value!);
    }
}
