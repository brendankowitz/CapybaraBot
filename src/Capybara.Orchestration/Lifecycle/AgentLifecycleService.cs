using System.Globalization;
using System.Text.RegularExpressions;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Diagnostics;
using Capybara.Orchestration.Configuration;
using Capybara.Policy.Audit;
using YamlDotNet.RepresentationModel;

namespace Capybara.Orchestration.Lifecycle;

/// <summary>
/// Human-only agent lifecycle (FR-AGT-07): create from a template, duplicate, hide, archive and delete. Every operation
/// is a validated change to the config tree through <see cref="CapybaraConfigurationHost.ApplyAsync"/> (so it is
/// reloaded and git-committed) and is audited.
/// </summary>
/// <remarks>
/// <para>Templates live in <c>{config}/templates/{name}/</c> with the same layout as an agent folder.</para>
/// <para>
/// Duplicating copies <c>agent.yaml</c>, <c>SOUL.md</c>, <c>AGENTS.md</c>, active skills (with their pins) and owned
/// routines under a new id. It never copies memory, sessions or <c>BaseDirectory</c>: those live under the state root,
/// and a new id is refused while <c>{state}/agents/{id}</c> still exists so it cannot inherit a previous agent's state.
/// </para>
/// <para>
/// Archive and delete also drop the agent's routines and its <c>rules.yaml</c> entries, remove it from other agents'
/// peer lists, then await every
/// <see cref="IAgentLifecycleObserver"/> (grant revoke, unschedule, runtime stop). Delete finally removes
/// <c>{state}/agents/{id}</c>. Memory is owned by the memory store; a memory observer is expected to clear
/// <c>/agents/{id}</c>.
/// </para>
/// </remarks>
public sealed partial class AgentLifecycleService(
    CapybaraConfigurationHost config,
    string stateRoot,
    IEnumerable<IAgentLifecycleObserver> observers,
    IAuditLog audit,
    TimeProvider time)
{
    public const string TemplatesFolder = "templates";

    /// <summary>Archived agents; the loader skips folders starting with <c>_</c>.</summary>
    public const string ArchiveFolder = "_archive";

    private const string AuditKind = "config_change";

    private static readonly string[] PeerLists = ["consult", "message", "task"];

    private readonly IAgentLifecycleObserver[] _observers = [.. observers];

    /// <summary><c>{state}/agents/{id}</c>: the agent's <c>BaseDirectory</c> and workspace.</summary>
    public string StateDirectory(AgentId agent) => Path.Combine(stateRoot, "agents", agent.Value);

    public IReadOnlyList<string> ListTemplates()
    {
        var dir = Path.Combine(config.Root, TemplatesFolder);
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateDirectories(dir).Where(d => File.Exists(Path.Combine(d, "agent.yaml"))).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Creates agent <paramref name="id"/> from <c>templates/{template}</c>.</summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="by"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException">The template does not exist.</exception>
    /// <exception cref="InvalidOperationException">The id is taken, or state from a previous agent with this id remains.</exception>
    public async ValueTask<ConfigChangeResult> CreateFromTemplateAsync(string template, AgentId id, string name, ConfigPrincipal by, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureHumanAsync(by, "create an agent", id, cancellationToken);
        if (!TemplateName().IsMatch(template) || !ListTemplates().Contains(template, StringComparer.Ordinal))
        {
            throw new KeyNotFoundException($"Unknown template '{template}'.");
        }

        EnsureNewId(id);
        var result = await config.ApplyAsync(
            root =>
            {
                CopyAgentFolder(Path.Combine(root, TemplatesFolder, template), ConfigTree.AgentDirectory(root, id.Value));
                Rename(root, id, name);
            },
            $"Create agent {id} from template {template} ({by.Name})",
            cancellationToken);
        await AuditAsync(result, "created", id, by, $"from template {template}");
        return result;
    }

    /// <summary>Copies <paramref name="source"/>'s profile, skills and routines to a new agent <paramref name="id"/>; never memory or history.</summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="by"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="source"/> is not a declared agent.</exception>
    /// <exception cref="InvalidOperationException">The id is taken, or state from a previous agent with this id remains.</exception>
    public async ValueTask<ConfigChangeResult> DuplicateAsync(AgentId source, AgentId id, string name, ConfigPrincipal by, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureHumanAsync(by, "duplicate an agent", id, cancellationToken);
        Require(source);
        EnsureNewId(id);
        var result = await config.ApplyAsync(
            root =>
            {
                CopyAgentFolder(ConfigTree.AgentDirectory(root, source.Value), ConfigTree.AgentDirectory(root, id.Value));
                Rename(root, id, name);
                var (original, routines) = ConfigTree.ReadRoutines(root);
                var copies = ConfigTree.RoutinesOwnedBy(routines, source.Value).Select(r => CopyRoutine(r, source, id)).ToList();
                if (copies.Count > 0)
                {
                    copies.ForEach(routines.Add);
                    ConfigTree.WriteRoutines(root, original, routines);
                }
            },
            $"Duplicate agent {source} as {id} ({by.Name})",
            cancellationToken);
        await AuditAsync(result, "duplicated", id, by, $"from {source}");
        return result;
    }

    /// <summary>Hidden agents keep running but are not listed in clients or <c>list_agents</c>.</summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="by"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="agent"/> is not a declared agent.</exception>
    public async ValueTask<ConfigChangeResult> SetHiddenAsync(AgentId agent, bool hidden, ConfigPrincipal by, CancellationToken cancellationToken = default)
    {
        await EnsureHumanAsync(by, "hide an agent", agent, cancellationToken);
        Require(agent);
        var result = await config.ApplyAsync(
            root => EditAgentYaml(root, agent, yaml =>
            {
                YamlEditor.SetScalar(yaml, "hidden", hidden ? "true" : "false");
                return true;
            }),
            $"{(hidden ? "Hide" : "Show")} agent {agent} ({by.Name})",
            cancellationToken);
        await AuditAsync(result, hidden ? "hidden" : "shown", agent, by, null);
        return result;
    }

    /// <summary>
    /// Moves the agent's config to <c>agents/_archive/</c> together with its routines and agent-scoped rules, so it
    /// stops running but can be restored by hand. Staged skill proposals are dropped. Runtime state is kept.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="by"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="agent"/> is not a declared agent.</exception>
    /// <exception cref="AggregateException">The config change applied, but an observer failed.</exception>
    public async ValueTask<ConfigChangeResult> ArchiveAsync(AgentId agent, ConfigPrincipal by, CancellationToken cancellationToken = default)
    {
        await EnsureHumanAsync(by, "archive an agent", agent, cancellationToken);
        var definition = Require(agent);
        var archiveName = Directory.Exists(Path.Combine(ConfigTree.AgentsDirectory(config.Root), ArchiveFolder, agent.Value))
            ? $"{agent}-{time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}"
            : agent.Value;

        var removed = FleetRemoval.None;
        var result = await config.ApplyAsync(
            root =>
            {
                var archive = Path.Combine(ConfigTree.AgentsDirectory(root), ArchiveFolder, archiveName);
                ConfigTree.CreateDirectory(Path.GetDirectoryName(archive)!);
                Directory.Move(RequireFolder(root, agent), archive);
                var pending = Path.Combine(archive, "skills", ConfigTree.PendingSkillsFolder);
                if (Directory.Exists(pending))
                {
                    Directory.Delete(pending, recursive: true);
                }

                removed = RemoveFromFleet(root, agent);
                ConfigTree.WriteAtomic(Path.Combine(archive, ConfigTree.RoutinesFile), YamlEditor.Serialize(new YamlSequenceNode(removed.Routines)));
                if (removed.Rules.Count > 0)
                {
                    ConfigTree.WriteAtomic(Path.Combine(archive, ConfigTree.RulesFile), YamlEditor.Serialize(new YamlSequenceNode(removed.Rules)));
                }
            },
            $"Archive agent {agent} ({by.Name})",
            cancellationToken);

        return await CompleteRemovalAsync(
            result,
            new AgentRemoval(agent, AgentRemovalKind.Archived, definition.Grants, removed.RoutineIds, by.Name),
            by,
            $"to {ArchiveFolder}/{archiveName}, {removed.Rules.Count} agent rule(s) archived");
    }

    /// <summary>
    /// Deletes the agent: removes its config, routines and the <c>rules.yaml</c> entries scoped exactly to it (so a
    /// later agent with the same id inherits none of them), drops it from peer lists, awaits observers (grant revoke,
    /// unschedule, runtime stop, stale proposals), then deletes <c>{state}/agents/{id}</c>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="by"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="agent"/> is not a declared agent.</exception>
    /// <exception cref="AggregateException">The config change applied, but an observer or the state cleanup failed.</exception>
    public async ValueTask<ConfigChangeResult> DeleteAsync(AgentId agent, ConfigPrincipal by, CancellationToken cancellationToken = default)
    {
        await EnsureHumanAsync(by, "delete an agent", agent, cancellationToken);
        var definition = Require(agent);
        var removed = FleetRemoval.None;
        var result = await config.ApplyAsync(
            root =>
            {
                Directory.Delete(RequireFolder(root, agent), recursive: true);
                removed = RemoveFromFleet(root, agent);
            },
            $"Delete agent {agent} ({by.Name})",
            cancellationToken);

        return await CompleteRemovalAsync(
            result,
            new AgentRemoval(agent, AgentRemovalKind.Deleted, definition.Grants, removed.RoutineIds, by.Name),
            by,
            $"{removed.Rules.Count} agent rule(s) removed");
    }

    /// <summary>
    /// Runs after the config change. Cleanup always runs to the end (no cancellation): the agent is already gone from
    /// the config, and stopping half-way would leave grants or routines live for an agent that no longer exists.
    /// </summary>
    private async ValueTask<ConfigChangeResult> CompleteRemovalAsync(ConfigChangeResult result, AgentRemoval removal, ConfigPrincipal by, string? detail)
    {
        var decision = removal.Kind == AgentRemovalKind.Deleted ? "deleted" : "archived";
        if (!result.Applied)
        {
            await AuditAsync(result, decision, removal.Agent, by, detail);
            return result;
        }

        using var activity = CapybaraTelemetry.Orchestration.StartActivity("agent.remove");
        activity?.SetTag("capybara.agent", removal.Agent.Value);
        var failures = new List<Exception>();
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnAgentRemovedAsync(removal, CancellationToken.None);
            }
#pragma warning disable CA1031 // One failing subscriber must not leave the others' grants or routines live; failures are reported below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add(ex);
            }
        }

        if (removal.Kind == AgentRemovalKind.Deleted)
        {
            var state = StateDirectory(removal.Agent);
            try
            {
                if (Directory.Exists(state))
                {
                    Directory.Delete(state, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(ex);
            }
        }

        var cleanup = failures.Count == 0 ? null : $"cleanup failed: {string.Join("; ", failures.Select(f => f.Message))}";
        await AuditAsync(result, failures.Count == 0 ? decision : decision + "_partial", removal.Agent, by, string.Join(' ', new[] { detail, cleanup }.OfType<string>()));
        return failures.Count == 0
            ? result
            : throw new AggregateException($"Agent {removal.Agent} was {decision} from the config, but cleanup failed.", failures);
    }

    /// <summary>Removes the agent's routines, its agent-scoped rules and its entries in other agents' peer lists.</summary>
    private static FleetRemoval RemoveFromFleet(string root, AgentId agent)
    {
        var (original, routines) = ConfigTree.ReadRoutines(root);
        var owned = ConfigTree.RoutinesOwnedBy(routines, agent.Value).ToList();
        if (owned.Count > 0)
        {
            owned.ForEach(r => routines.Children.Remove(r));
            ConfigTree.WriteRoutines(root, original, routines);
        }

        foreach (var dir in Directory.EnumerateDirectories(ConfigTree.AgentsDirectory(root)))
        {
            var folder = Path.GetFileName(dir);
            if (!folder.StartsWith('_') && !folder.StartsWith('.') && AgentId.TryParse(folder, out var other))
            {
                EditAgentYaml(root, other, yaml => RemovePeer(yaml, agent));
            }
        }

        return new FleetRemoval(owned, ConfigTree.RemoveRulesFor(root, agent.Value));
    }

    /// <summary>Returns whether a peer entry was removed; the file is only rewritten when it was.</summary>
    private static bool RemovePeer(YamlMappingNode yaml, AgentId agent)
    {
        if (YamlEditor.Get(yaml, "peers") is not YamlMappingNode peers)
        {
            return false;
        }

        var changed = false;
        foreach (var list in PeerLists)
        {
            if (YamlEditor.Get(peers, list) is YamlSequenceNode sequence)
            {
                var matches = sequence.Children.OfType<YamlScalarNode>().Where(s => s.Value == agent.Value).ToList();
                matches.ForEach(m => sequence.Children.Remove(m));
                changed |= matches.Count > 0;
            }
        }

        return changed;
    }

    /// <summary>Rewrites <c>agent.yaml</c> when <paramref name="edit"/> reports a change, keeping its header comments.</summary>
    private static void EditAgentYaml(string root, AgentId agent, Func<YamlMappingNode, bool> edit)
    {
        var file = ConfigTree.AgentYaml(root, agent.Value);
        if (!File.Exists(file))
        {
            return;
        }

        var original = File.ReadAllText(file);
        var yaml = YamlEditor.ParseMapping(original);
        if (edit(yaml))
        {
            ConfigTree.WriteAtomic(file, YamlEditor.SerializeWithHeader(original, yaml));
        }
    }

    private static void Rename(string root, AgentId id, string name) =>
        EditAgentYaml(root, id, yaml =>
        {
            YamlEditor.SetScalar(yaml, "id", id.Value);
            YamlEditor.SetScalar(yaml, "name", name);
            return true;
        });

    /// <summary>Copies an agent folder, leaving out staged skill proposals, which belong to the source agent.</summary>
    private static void CopyAgentFolder(string source, string destination)
    {
        // Checked inside the edit, under the host's write lock, so two concurrent creates cannot merge into one folder.
        if (Directory.Exists(destination))
        {
            throw new CapybaraConfigurationException($"agents/{Path.GetFileName(destination)} already exists.");
        }

        CapybaraConfigurationHost.CopyDirectory(source, destination, path =>
            !(Path.GetFileName(path) == "pending" && Path.GetFileName(Path.GetDirectoryName(path)) == "skills"));
    }

    private static string RequireFolder(string root, AgentId agent)
    {
        var dir = ConfigTree.AgentDirectory(root, agent.Value);
        return Directory.Exists(dir) ? dir : throw new CapybaraConfigurationException($"agent '{agent}' no longer exists.");
    }

    /// <summary>Routine ids are global: <c>ea-heartbeat</c> becomes <c>ea2-heartbeat</c>, other ids get the new id as a prefix.</summary>
    private static YamlMappingNode CopyRoutine(YamlMappingNode routine, AgentId source, AgentId target)
    {
        var copy = YamlEditor.ParseMapping(YamlEditor.Serialize(routine));
        var id = YamlEditor.GetScalar(routine, "id") ?? string.Empty;
        var prefix = source.Value + "-";
        YamlEditor.SetScalar(copy, "id", id.StartsWith(prefix, StringComparison.Ordinal) ? target.Value + "-" + id[prefix.Length..] : $"{target}-{id}");
        YamlEditor.SetScalar(copy, "owner", target.Value);
        return copy;
    }

    private AgentDefinition Require(AgentId agent) =>
        config.Current.Agents.FirstOrDefault(a => a.Id == agent) ?? throw new KeyNotFoundException($"Unknown agent '{agent}'.");

    private void EnsureNewId(AgentId id)
    {
        if (config.Current.Agents.Any(a => a.Id == id) || Directory.Exists(ConfigTree.AgentDirectory(config.Root, id.Value)))
        {
            throw new InvalidOperationException($"Agent '{id}' already exists.");
        }

        if (Directory.Exists(StateDirectory(id)))
        {
            throw new InvalidOperationException(
                $"State from a previous agent '{id}' still exists; choose another id or delete it first, so the new agent cannot inherit its sessions or workspace.");
        }
    }

    private async ValueTask EnsureHumanAsync(ConfigPrincipal by, string action, AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!by.IsHuman)
        {
            await audit.AppendAsync(new AuditRecord(AuditKind, "denied", agent.Value, Detail: $"{by.Origin} principal '{by.Name}' tried to {action}"), cancellationToken);
            by.EnsureHuman(action);
        }
    }

    private async ValueTask AuditAsync(ConfigChangeResult result, string decision, AgentId agent, ConfigPrincipal by, string? detail)
    {
        var parts = new List<string> { $"by {by.Name}" };
        if (detail is not null)
        {
            parts.Add(detail);
        }

        if (!result.Applied)
        {
            parts.Add("invalid: " + string.Join("; ", result.Errors));
        }
        else if (result.VersioningError is not null)
        {
            parts.Add("config commit failed: " + result.VersioningError);
        }

        await audit.AppendAsync(new AuditRecord(AuditKind, result.Applied ? decision : "invalid", agent.Value, Detail: string.Join(", ", parts)), CancellationToken.None);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateName();

    /// <summary>What <see cref="RemoveFromFleet"/> took out of <c>routines.yaml</c> and <c>rules.yaml</c>.</summary>
    private sealed record FleetRemoval(IReadOnlyList<YamlMappingNode> Routines, IReadOnlyList<YamlMappingNode> Rules)
    {
        public static readonly FleetRemoval None = new([], []);

        public IReadOnlyList<string> RoutineIds => [.. Routines.Select(r => YamlEditor.GetScalar(r, "id")).OfType<string>()];
    }
}
