using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Versioning;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Orchestration.Tests;

/// <summary>
/// A small, self-contained config tree (agents <c>alpha</c> and <c>beta</c>, one routine each) with a live
/// <see cref="CapybaraConfigurationHost"/>, plus a state root.
/// </summary>
internal sealed class ConfigFixture : IDisposable
{
    public const string AlphaYaml = """
        # Alpha header comment.
        id: alpha
        name: Alpha
        job: Reads mail.
        tools: [list_agents, email_read]
        trifecta:
          untrustedInput: true
          privateData: true
          externalAction: false
        peers:
          consult: [beta]
        grants:
          - connection: gmail
            operations: [read]
        """;

    public const string BetaYaml = """
        id: beta
        name: Beta
        job: Researches.
        peers:
          consult: [alpha]
          message: [alpha]
        accepts:
          consults: true
        """;

    public const string RoutinesYaml = """
        # Routines header.
        - id: alpha-heartbeat
          owner: alpha
          kind: heartbeat
          every: 30m
          prompt: Check mail.
        - id: beta-digest
          owner: beta
          kind: heartbeat
          every: 1h
          prompt: Digest.
        """;

    public const string ValidSkill = """
        ---
        name: triage
        description: Triage the inbox.
        ---
        # When to use
        Mornings.
        # Inputs
        None.
        # Steps
        Read, sort.
        # Validation
        Nothing missed.
        # Outputs
        A list.
        # Approval boundary
        None.
        """;

    private readonly TempDirectory _dir = new();

    public ConfigFixture()
    {
        ConfigRoot = Path.Combine(_dir.Path, "config");
        StateRoot = Path.Combine(_dir.Path, "state");
        Write("agents/alpha/agent.yaml", AlphaYaml);
        Write("agents/alpha/SOUL.md", "You are Alpha.\nCalm and brief.\n");
        Write("agents/alpha/AGENTS.md", "Never send mail.\n");
        Write("agents/beta/agent.yaml", BetaYaml);
        Write("routines.yaml", RoutinesYaml);

        var initial = CapybaraConfigurationLoader.Load(ConfigRoot);
        KillSwitch = new KillSwitch(Time);
        Registry = new AgentRegistry(initial.Agents, KillSwitch);
        Host = new CapybaraConfigurationHost(ConfigRoot, initial, Registry, new RulesEngine(initial.Rules), Versioning);
    }

    public string ConfigRoot { get; }

    public string StateRoot { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public KillSwitch KillSwitch { get; }

    public AgentRegistry Registry { get; }

    public RecordingVersioning Versioning { get; } = new();

    public CapybaraConfigurationHost Host { get; }

    public InMemoryAuditLog Audit { get; } = new();

    public string PathOf(string relative) => Path.Combine(ConfigRoot, relative);

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    public void Dispose()
    {
        Host.Dispose();
        KillSwitch.Dispose();
        _dir.Dispose();
    }

    internal sealed class RecordingVersioning : IConfigVersioning
    {
        public List<string> Commits { get; } = [];

        /// <summary>When set, commits throw this, as a missing or broken git would.</summary>
        public Exception? Failure { get; set; }

        public ValueTask CommitAsync(string message, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Commits.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
