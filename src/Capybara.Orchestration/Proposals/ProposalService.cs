using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Diagnostics;
using Capybara.Contracts.Proposals;
using Capybara.Orchestration.Configuration;
using Capybara.Orchestration.Skills;
using Capybara.Policy.Audit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Capybara.Orchestration.Proposals;

/// <summary>
/// The only path by which an agent can influence its own persona, config, routines or skills (FR-AGT-06, FR-SAF-11,
/// FR-RTN-04, FR-SKL-03). <see cref="ProposeAsync"/> records a diff against a base hash; nothing changes until a human
/// approves it through <see cref="DecideAsync"/>, which re-checks the base, validates the whole resulting config and
/// applies it through <see cref="CapybaraConfigurationHost.ApplyAsync"/> (atomic writes, reload, git commit).
/// </summary>
/// <remarks>
/// Grants, connections and channels are never proposable (FR-CON-07). An agent can only target its own files: the
/// target is derived from the proposing agent's id, never from a path it supplies. Thread-safe: proposals and
/// decisions are serialized.
/// </remarks>
public sealed partial class ProposalService(IProposalStore store, CapybaraConfigurationHost config, IAuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>Caps how many undecided proposals one agent can put in the human's inbox.</summary>
    public const int MaxPendingPerAgent = 20;

    public const int MaxRationaleLength = 2000;

    /// <summary><c>agent.yaml</c> keys a proposal may not change: grants and anything that is a connection or channel.</summary>
    private static readonly string[] ProtectedAgentKeys = ["grants", "connections", "channels", "mcpServers", "provider"];

    private const string AuditKind = "config_change";

    /// <summary>Staging folder for skill proposals inside an agent's skill tier (FR-SKL-03).</summary>
    private const string PendingFolder = "pending";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after a proposal is recorded, so it can be pushed to the approval inbox.</summary>
    public event EventHandler<ChangeProposal>? Proposed;

    /// <summary>Raised after a human settles a proposal (any terminal status).</summary>
    public event EventHandler<ChangeProposal>? Decided;

    /// <summary>
    /// Records a proposal from <paramref name="agent"/> for its own files. <paramref name="name"/> is the routine id or
    /// skill name for <see cref="ProposalKind.Routine"/> and <see cref="ProposalKind.Skill"/>, ignored otherwise.
    /// For <see cref="ProposalKind.Routine"/> the content is one YAML mapping; for the others it is the whole file.
    /// </summary>
    /// <exception cref="ProposalRejectedException">The proposal is not allowed or would not validate; it is audited, not recorded.</exception>
    public async ValueTask<ChangeProposal> ProposeAsync(
        AgentId agent,
        ProposalKind kind,
        string? name,
        string content,
        string rationale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rationale);
        using var activity = CapybaraTelemetry.Orchestration.StartActivity("proposal.propose");
        activity?.SetTag("capybara.agent", agent.Value);
        activity?.SetTag("capybara.proposal.kind", kind.ToString());

        ChangeProposal proposal;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                proposal = await CreateAsync(agent, kind, name, content, rationale, cancellationToken);
            }
            catch (ProposalRejectedException ex)
            {
                await audit.AppendAsync(new AuditRecord(AuditKind, "proposal_rejected", agent.Value, Detail: $"{kind} {name}: {ex.Message}"), cancellationToken);
                throw;
            }

            if (kind == ProposalKind.Skill)
            {
                ConfigTree.WriteAtomic(Path.Combine(PendingSkillDirectory(config.Root, agent.Value, proposal.Name!), "SKILL.md"), proposal.ProposedContent);
            }

            await store.SaveAsync(proposal, cancellationToken);
            await audit.AppendAsync(new AuditRecord(AuditKind, "proposed", agent.Value, Detail: $"proposal {proposal.Id}: {kind} {proposal.TargetPath}"), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        Proposed?.Invoke(this, proposal);
        return proposal;
    }

    /// <summary>
    /// Settles a pending proposal. Only a human may decide. On approval the base hash is re-checked
    /// (<see cref="ProposalStatus.Stale"/>), the resulting config is validated (<see cref="ProposalStatus.Invalid"/>,
    /// nothing written), then the change is written, reloaded, committed and audited (<see cref="ProposalStatus.Applied"/>).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException"><paramref name="decidedBy"/> is not a human.</exception>
    /// <exception cref="KeyNotFoundException">No proposal has this id.</exception>
    /// <exception cref="InvalidOperationException">The proposal was already decided.</exception>
    public async ValueTask<ChangeProposal> DecideAsync(string id, bool approve, ConfigPrincipal decidedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decidedBy);
        using var activity = CapybaraTelemetry.Orchestration.StartActivity("proposal.decide");
        if (!decidedBy.IsHuman)
        {
            await audit.AppendAsync(new AuditRecord(AuditKind, "denied", decidedBy.Name, Detail: $"proposal {id}: decision by a non-human principal"), cancellationToken);
            decidedBy.EnsureHuman("decide a proposal");
        }

        ChangeProposal decided;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var proposal = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Unknown proposal '{id}'.");
            if (proposal.Status != ProposalStatus.Pending)
            {
                throw new InvalidOperationException($"Proposal '{id}' is already {proposal.Status}.");
            }

            var (status, error) = approve ? await ApproveAsync(proposal, decidedBy, cancellationToken) : (ProposalStatus.Rejected, null);
            if (proposal.Kind == ProposalKind.Skill && status != ProposalStatus.Applied)
            {
                RemovePendingSkill(config.Root, proposal.Agent.Value, proposal.Name!);
            }

            decided = proposal with { Status = status, DecidedBy = decidedBy.Name, DecidedAt = time.GetUtcNow(), Error = error };
            await store.SaveAsync(decided, cancellationToken);
            await audit.AppendAsync(
                new AuditRecord(AuditKind, status.ToString().ToLowerInvariant(), proposal.Agent.Value, Detail: $"proposal {id}: {proposal.Kind} {proposal.TargetPath} by {decidedBy.Name}{(error is null ? string.Empty : ": " + error)}"),
                cancellationToken);
            activity?.SetTag("capybara.proposal.status", status.ToString());
        }
        finally
        {
            _lock.Release();
        }

        Decided?.Invoke(this, decided);
        return decided;
    }

    public ValueTask<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken = default) => store.GetAsync(id, cancellationToken);

    /// <summary>Proposals oldest first, optionally only those with <paramref name="status"/>.</summary>
    public async ValueTask<IReadOnlyList<ChangeProposal>> ListAsync(ProposalStatus? status = null, CancellationToken cancellationToken = default)
    {
        var all = new List<ChangeProposal>();
        await foreach (var proposal in store.ListAsync(cancellationToken))
        {
            if (status is null || proposal.Status == status)
            {
                all.Add(proposal);
            }
        }

        return [.. all.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id, StringComparer.Ordinal)];
    }

    public void Dispose() => _lock.Dispose();

    private async ValueTask<ChangeProposal> CreateAsync(AgentId agent, ProposalKind kind, string? name, string content, string rationale, CancellationToken cancellationToken)
    {
        if (config.Current.Agents.All(a => a.Id != agent))
        {
            throw new ProposalRejectedException($"'{agent}' is not a declared agent.");
        }

        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleLength)
        {
            throw new ProposalRejectedException($"A rationale of 1 to {MaxRationaleLength} characters is required.");
        }

        if (Encoding.UTF8.GetByteCount(content) > SkillScanner.MaxFileBytes)
        {
            throw new ProposalRejectedException($"Proposed content is larger than {SkillScanner.MaxFileBytes} bytes.");
        }

        name = kind is ProposalKind.Routine or ProposalKind.Skill ? name?.Trim() : null;
        var (targetPath, proposed) = Resolve(agent, kind, name, content);

        var pending = (await ListAsync(ProposalStatus.Pending, cancellationToken)).Where(p => p.Agent == agent).ToList();
        if (pending.Any(p => p.Kind == kind && p.Name == name))
        {
            throw new ProposalRejectedException($"A proposal for {targetPath}{(name is null ? string.Empty : " (" + name + ")")} is already pending.");
        }

        if (pending.Count >= MaxPendingPerAgent)
        {
            throw new ProposalRejectedException($"{agent} already has {MaxPendingPerAgent} pending proposals.");
        }

        var draft = new ChangeProposal
        {
            Id = Guid.NewGuid().ToString("N"),
            Agent = agent,
            Kind = kind,
            Name = name,
            TargetPath = targetPath,
            ProposedContent = proposed,
            Diff = string.Empty,
            BaseHash = string.Empty,
            Rationale = rationale.Trim(),
            CreatedAt = time.GetUtcNow(),
        };

        var baseContent = ReadBase(config.Root, draft);
        string diff;
        try
        {
            diff = LineDiff.Unified(baseContent, proposed, kind == ProposalKind.Routine ? $"{targetPath}#{name}" : targetPath);
        }
        catch (ArgumentException ex)
        {
            throw new ProposalRejectedException(ex.Message, ex);
        }

        draft = draft with { Diff = diff, BaseHash = Hash(baseContent) };

        // Validate now as well as on approval, so the human never reviews a change that cannot apply.
        IReadOnlyList<string> errors;
        try
        {
            errors = config.Validate(root => ApplyTo(root, draft));
        }
        catch (StaleProposalException ex)
        {
            throw new ProposalRejectedException("The target changed while the proposal was being made; retry.", ex);
        }

        return errors.Count == 0 ? draft : throw new ProposalRejectedException("The change would not validate: " + string.Join("; ", errors));
    }

    /// <summary>Maps a proposal to its target path (relative to the config root) and the exact content that will be written.</summary>
    private (string TargetPath, string Content) Resolve(AgentId agent, ProposalKind kind, string? name, string content)
    {
        var dir = $"agents/{agent.Value}";
        switch (kind)
        {
            case ProposalKind.Soul:
                return ($"{dir}/SOUL.md", content);
            case ProposalKind.OperatingRules:
                return ($"{dir}/AGENTS.md", content);
            case ProposalKind.AgentConfig:
                CheckAgentConfig(agent, content);
                return ($"{dir}/agent.yaml", content);
            case ProposalKind.Routine:
                return (ConfigTree.RoutinesFile, YamlEditor.Serialize(CheckRoutine(agent, name, content)));
            case ProposalKind.Skill:
                CheckSkill(name, content);
                return ($"{dir}/skills/{name}/SKILL.md", content);
            default:
                throw new ProposalRejectedException($"Unknown proposal kind '{kind}'.");
        }
    }

    private void CheckAgentConfig(AgentId agent, string content)
    {
        var proposed = ParseMapping(content, "agent.yaml");
        if (YamlEditor.GetScalar(proposed, "id") != agent.Value)
        {
            throw new ProposalRejectedException($"agent.yaml must keep id '{agent}'; an agent can only propose changes to its own files.");
        }

        var current = YamlEditor.ParseMapping(File.ReadAllText(ConfigTree.AgentYaml(config.Root, agent.Value)));
        foreach (var key in ProtectedAgentKeys)
        {
            if (!YamlEditor.EquivalentOrEmpty(YamlEditor.Get(current, key), YamlEditor.Get(proposed, key)))
            {
                throw new ProposalRejectedException($"'{key}' cannot be changed by a proposal; connections, grants and channels are changed only by the user.");
            }
        }
    }

    private YamlMappingNode CheckRoutine(AgentId agent, string? name, string content)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ProposalRejectedException("A routine proposal needs the routine id as its name.");
        }

        var routine = ParseMapping(content, "routine");
        if (YamlEditor.GetScalar(routine, "id") != name)
        {
            throw new ProposalRejectedException($"The routine's id must be '{name}'.");
        }

        if (YamlEditor.GetScalar(routine, "owner") != agent.Value)
        {
            throw new ProposalRejectedException($"The routine's owner must be '{agent}'; an agent can only propose its own routines.");
        }

        var existing = ConfigTree.FindRoutine(ConfigTree.ReadRoutines(config.Root).Routines, name);
        if (existing is not null && YamlEditor.GetScalar(existing, "owner") != agent.Value)
        {
            throw new ProposalRejectedException($"Routine '{name}' belongs to another agent.");
        }

        return routine;
    }

    private static void CheckSkill(string? name, string content)
    {
        if (name is null || name == PendingFolder || !SkillName().IsMatch(name))
        {
            throw new ProposalRejectedException("A skill proposal needs a name of lower-case letters, digits and dashes.");
        }

        var findings = SkillScanner.ScanSkill(content);
        if (SkillScanner.Blocks(findings))
        {
            throw new ProposalRejectedException("The skill fails the static scan: " + string.Join("; ", findings.Where(f => f.Severity == SkillFindingSeverity.Block).Select(f => f.Message)));
        }
    }

    private async ValueTask<(ProposalStatus Status, string? Error)> ApproveAsync(ChangeProposal proposal, ConfigPrincipal decidedBy, CancellationToken cancellationToken)
    {
        try
        {
            var result = await config.ApplyAsync(
                root => ApplyTo(root, proposal),
                $"Apply proposal {proposal.Id} from {proposal.Agent}: {proposal.Kind} {proposal.TargetPath} (approved by {decidedBy.Name})",
                cancellationToken);
            return result switch
            {
                { Applied: false } => (ProposalStatus.Invalid, string.Join("; ", result.Errors)),
                { VersioningError: { } versioning } => (ProposalStatus.Applied, "applied, but the config commit failed: " + versioning),
                _ => (ProposalStatus.Applied, null),
            };
        }
        catch (StaleProposalException ex)
        {
            return (ProposalStatus.Stale, ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="proposal"/> into the config tree at <paramref name="root"/> (a scratch copy or the live
    /// tree). Throws <see cref="StaleProposalException"/> when the target no longer matches the base hash, unless it
    /// already holds the proposed content (re-applying after a crash is a no-op).
    /// </summary>
    private static void ApplyTo(string root, ChangeProposal proposal)
    {
        var current = ReadBase(root, proposal);
        if (Hash(current) != proposal.BaseHash && current != proposal.ProposedContent)
        {
            throw new StaleProposalException($"{proposal.TargetPath} changed since the proposal was made.");
        }

        switch (proposal.Kind)
        {
            case ProposalKind.Routine:
                var (original, routines) = ConfigTree.ReadRoutines(root);
                var routine = YamlEditor.ParseMapping(proposal.ProposedContent);
                var existing = ConfigTree.FindRoutine(routines, proposal.Name!);
                if (existing is null)
                {
                    routines.Add(routine);
                }
                else
                {
                    routines.Children[routines.Children.IndexOf(existing)] = routine;
                }

                ConfigTree.WriteRoutines(root, original, routines);
                break;
            case ProposalKind.Skill:
                ActivateSkill(root, proposal);
                break;
            default:
                ConfigTree.WriteAtomic(Path.Combine(root, proposal.TargetPath), proposal.ProposedContent);
                break;
        }
    }

    /// <summary>
    /// FR-SKL-03/04: writes the reviewed content (not whatever is in <c>pending/</c> now) to the active folder, drops
    /// the staged copy, requires the whole folder to pass the static scan, and pins it in the tier's lock file.
    /// </summary>
    private static void ActivateSkill(string root, ChangeProposal proposal)
    {
        var agent = proposal.Agent.Value;
        var skillsDir = ConfigTree.SkillsDirectory(root, agent);
        var skillDir = Path.Combine(skillsDir, proposal.Name!);
        ConfigTree.WriteAtomic(Path.Combine(skillDir, "SKILL.md"), proposal.ProposedContent);
        RemovePendingSkill(root, agent, proposal.Name!);

        var findings = SkillScanner.ScanDirectory(skillDir);
        if (SkillScanner.Blocks(findings))
        {
            throw new CapybaraConfigurationException([.. findings.Where(f => f.Severity == SkillFindingSeverity.Block).Select(f => $"skill {proposal.Name}: {f.File}: {f.Message}")]);
        }

        SkillLock.Pin(skillsDir, proposal.Name!);
    }

    /// <summary>The target's current content: the file, or for a routine its serialized mapping. Null when absent.</summary>
    private static string? ReadBase(string root, ChangeProposal proposal) => proposal.Kind == ProposalKind.Routine
        ? ConfigTree.FindRoutine(ConfigTree.ReadRoutines(root).Routines, proposal.Name!) is { } routine ? YamlEditor.Serialize(routine) : null
        : ConfigTree.ReadOptional(Path.Combine(root, proposal.TargetPath));

    private static YamlMappingNode ParseMapping(string content, string what)
    {
        try
        {
            return YamlEditor.ParseMapping(content);
        }
        catch (YamlException ex)
        {
            throw new ProposalRejectedException($"The proposed {what} is not a valid YAML mapping: {ex.Message}", ex);
        }
    }

    private static string Hash(string? content) =>
        content is null ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string PendingSkillDirectory(string root, string agent, string name) =>
        Path.Combine(ConfigTree.SkillsDirectory(root, agent), PendingFolder, name);

    private static void RemovePendingSkill(string root, string agent, string name)
    {
        var dir = PendingSkillDirectory(root, agent, name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        var pending = Path.GetDirectoryName(dir)!;
        if (Directory.Exists(pending) && !Directory.EnumerateFileSystemEntries(pending).Any())
        {
            Directory.Delete(pending);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();

    /// <summary>The target no longer matches the proposal's base; thrown out of the edit to abort the apply.</summary>
    private sealed class StaleProposalException(string message) : Exception(message);
}
