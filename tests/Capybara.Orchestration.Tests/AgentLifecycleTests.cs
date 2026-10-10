using Capybara.Contracts.Agents;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Lifecycle;
using Capybara.Orchestration.Skills;

namespace Capybara.Orchestration.Tests;

public sealed class AgentLifecycleTests : IDisposable
{
    private static readonly AgentId Alpha = AgentId.Parse("alpha");
    private static readonly AgentId Beta = AgentId.Parse("beta");
    private static readonly ConfigPrincipal User = ConfigPrincipal.Human("brendan");

    private readonly ConfigFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RecordingObserver _observer = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Duplicate_copies_profile_skills_and_routines_but_never_memory_or_state()
    {
        _fixture.Write("agents/alpha/skills/triage/SKILL.md", ConfigFixture.ValidSkill);
        SkillLock.Pin(_fixture.PathOf("agents/alpha/skills"), "triage");
        _fixture.Write("agents/alpha/skills/pending/draft/SKILL.md", ConfigFixture.ValidSkill);
        WriteState("agents/alpha/copilot/session-state/s1.json");
        WriteState("agents/alpha/workspace/notes.txt");
        WriteState("memory/agents/alpha/procedures/triage.md");
        var service = NewService();

        var result = await service.DuplicateAsync(Alpha, AgentId.Parse("alpha2"), "Alpha Two", User, Ct);

        Assert.True(result.Applied, string.Join('\n', result.Errors));
        var copy = _fixture.Registry.Get(AgentId.Parse("alpha2"));
        Assert.Equal("Alpha Two", copy.Name);
        Assert.Equal(_fixture.Registry.Get(Alpha).Soul, copy.Soul);
        Assert.Equal(_fixture.Registry.Get(Alpha).OperatingRules, copy.OperatingRules);
        Assert.Equal("triage", Assert.Single(copy.Skills).Name);
        Assert.False(Directory.Exists(_fixture.PathOf("agents/alpha2/skills/pending")));
        Assert.Equal(SkillTier.Agent, Assert.Single(new SkillResolver(Path.Combine(_fixture.StateRoot, "builtin"), _fixture.ConfigRoot).Resolve(copy.Id).Active).Tier);
        var routine = Assert.Single(_fixture.Host.Current.Routines, r => r.Owner == copy.Id);
        Assert.Equal("alpha2-heartbeat", routine.Id);
        Assert.StartsWith("# Alpha header comment.", _fixture.Read("agents/alpha2/agent.yaml"), StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(_fixture.StateRoot, "agents", "alpha2")));
        Assert.False(Directory.Exists(Path.Combine(_fixture.StateRoot, "memory", "agents", "alpha2")));
        Assert.Contains(_fixture.Audit.Entries, e => e is { Decision: "duplicated", Agent: "alpha2" });
        Assert.Single(_fixture.Versioning.Commits);
    }

    [Fact]
    public async Task Concurrent_creates_with_one_id_produce_one_agent()
    {
        var service = NewService();
        var gamma = AgentId.Parse("gamma");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            try
            {
                return (await service.DuplicateAsync(Alpha, gamma, $"Gamma {i}", User, Ct)).Applied;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, Ct)));

        Assert.Equal(1, outcomes.Count(applied => applied));
        Assert.Single(_fixture.Host.Current.Routines, r => r.Owner == gamma);
    }

    [Fact]
    public async Task Duplicate_refuses_an_id_whose_old_state_remains()
    {
        WriteState("agents/gamma/copilot/session-state/s1.json");
        var service = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DuplicateAsync(Alpha, AgentId.Parse("gamma"), "Gamma", User, Ct).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DuplicateAsync(Alpha, Beta, "Beta", User, Ct).AsTask());
        Assert.False(Directory.Exists(_fixture.PathOf("agents/gamma")));
    }

    [Fact]
    public async Task Delete_removes_config_routines_peers_and_state_and_notifies_observers()
    {
        WriteState("agents/alpha/copilot/session-state/s1.json");
        WriteState("agents/alpha/workspace/notes.txt");
        var stateDir = Path.Combine(_fixture.StateRoot, "agents", "alpha");
        _observer.OnRemoved = _ => Assert.True(Directory.Exists(stateDir), "observers run before the state directory is removed");
        var service = NewService();

        var result = await service.DeleteAsync(Alpha, User, Ct);

        Assert.True(result.Applied, string.Join('\n', result.Errors));
        Assert.False(Directory.Exists(_fixture.PathOf("agents/alpha")));
        Assert.False(Directory.Exists(stateDir));
        Assert.Null(_fixture.Registry.Find(Alpha));
        Assert.Equal(["beta-digest"], _fixture.Host.Current.Routines.Select(r => r.Id));
        Assert.StartsWith("# Routines header.", _fixture.Read("routines.yaml"), StringComparison.Ordinal);
        var beta = _fixture.Registry.Get(Beta);
        Assert.Empty(beta.Peers.Consult);
        Assert.Empty(beta.Peers.Message);

        var removal = Assert.Single(_observer.Removals);
        Assert.Equal(AgentRemovalKind.Deleted, removal.Kind);
        Assert.Equal(Alpha, removal.Agent);
        Assert.Equal("gmail", Assert.Single(removal.Grants).Connection);
        Assert.Equal(["alpha-heartbeat"], removal.Routines);
        Assert.Equal("brendan", removal.RemovedBy);
        Assert.Contains(_fixture.Audit.Entries, e => e is { Decision: "deleted", Agent: "alpha" });
    }

    [Fact]
    public async Task Delete_reports_observer_failures_after_running_every_cleanup_step()
    {
        WriteState("agents/alpha/workspace/notes.txt");
        var failing = new RecordingObserver { OnRemoved = _ => throw new InvalidOperationException("broker unreachable") };
        var service = new AgentLifecycleService(_fixture.Host, _fixture.StateRoot, [failing, _observer], _fixture.Audit, _fixture.Time);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => service.DeleteAsync(Alpha, User, Ct).AsTask());

        Assert.Contains(ex.InnerExceptions, e => e.Message == "broker unreachable");
        Assert.Single(_observer.Removals);
        Assert.False(Directory.Exists(Path.Combine(_fixture.StateRoot, "agents", "alpha")));
        Assert.Contains(_fixture.Audit.Entries, e => e.Decision == "deleted_partial");
    }

    [Fact]
    public async Task Agent_principal_cannot_run_lifecycle_operations()
    {
        var service = NewService();
        var agent = ConfigPrincipal.Agent(Beta);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(Alpha, agent, Ct).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ArchiveAsync(Alpha, agent, Ct).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DuplicateAsync(Alpha, AgentId.Parse("alpha2"), "x", agent, Ct).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetHiddenAsync(Alpha, true, agent, Ct).AsTask());

        Assert.NotNull(_fixture.Registry.Find(Alpha));
        Assert.Empty(_observer.Removals);
        Assert.Empty(_fixture.Versioning.Commits);
        Assert.Equal(4, _fixture.Audit.Entries.Count(e => e.Decision == "denied"));
    }

    [Fact]
    public async Task Archive_moves_config_and_routines_aside_and_keeps_state()
    {
        WriteState("agents/alpha/workspace/notes.txt");
        var service = NewService();

        var result = await service.ArchiveAsync(Alpha, User, Ct);

        Assert.True(result.Applied, string.Join('\n', result.Errors));
        Assert.Null(_fixture.Registry.Find(Alpha));
        Assert.True(File.Exists(_fixture.PathOf("agents/_archive/alpha/agent.yaml")));
        Assert.Contains("alpha-heartbeat", _fixture.Read("agents/_archive/alpha/routines.yaml"), StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.Host.Current.Routines, r => r.Id == "alpha-heartbeat");
        Assert.True(File.Exists(Path.Combine(_fixture.StateRoot, "agents", "alpha", "workspace", "notes.txt")));
        Assert.Equal(AgentRemovalKind.Archived, Assert.Single(_observer.Removals).Kind);
    }

    [Fact]
    public async Task Hide_and_show_toggle_the_hidden_flag()
    {
        var service = NewService();

        await service.SetHiddenAsync(Alpha, hidden: true, User, Ct);
        Assert.True(_fixture.Registry.Get(Alpha).Hidden);
        Assert.Empty(_fixture.Registry.ListFor(Beta));
        Assert.StartsWith("# Alpha header comment.", _fixture.Read("agents/alpha/agent.yaml"), StringComparison.Ordinal);

        await service.SetHiddenAsync(Alpha, hidden: false, User, Ct);
        Assert.False(_fixture.Registry.Get(Alpha).Hidden);
        Assert.Equal(2, _fixture.Versioning.Commits.Count);
    }

    [Fact]
    public async Task Create_from_template_adds_a_new_agent()
    {
        _fixture.Write("templates/helper/agent.yaml", "id: template\nname: Template\njob: Helps.\n");
        _fixture.Write("templates/helper/SOUL.md", "Helpful.\n");
        var service = NewService();

        Assert.Equal(["helper"], service.ListTemplates());
        var result = await service.CreateFromTemplateAsync("helper", AgentId.Parse("helper-one"), "Helper One", User, Ct);

        Assert.True(result.Applied, string.Join('\n', result.Errors));
        var created = _fixture.Registry.Get(AgentId.Parse("helper-one"));
        Assert.Equal("Helper One", created.Name);
        Assert.Equal("Helpful.\n", created.Soul);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateFromTemplateAsync("../agents/alpha", AgentId.Parse("x-two"), "x", User, Ct).AsTask());
    }

    [Fact]
    public async Task Invalid_template_writes_nothing()
    {
        _fixture.Write("templates/broken/agent.yaml", "id: template\nname: Broken\n");
        var service = NewService();

        var result = await service.CreateFromTemplateAsync("broken", AgentId.Parse("broken-one"), "Broken", User, Ct);

        Assert.False(result.Applied);
        Assert.False(Directory.Exists(_fixture.PathOf("agents/broken-one")));
        Assert.Empty(_fixture.Versioning.Commits);
        Assert.Contains(_fixture.Audit.Entries, e => e.Decision == "invalid");
    }

    private AgentLifecycleService NewService() => new(_fixture.Host, _fixture.StateRoot, [_observer], _fixture.Audit, _fixture.Time);

    private void WriteState(string relative)
    {
        var path = Path.Combine(_fixture.StateRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "state");
    }

    private sealed class RecordingObserver : IAgentLifecycleObserver
    {
        public List<AgentRemoval> Removals { get; } = [];

        public Action<AgentRemoval>? OnRemoved { get; set; }

        public ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
        {
            Removals.Add(removal);
            OnRemoved?.Invoke(removal);
            return ValueTask.CompletedTask;
        }
    }
}
