using Fleet.Contracts.Agents;

namespace Fleet.Orchestration.Skills;

public enum SkillTier
{
    BuiltIn,
    Shared,
    Agent,
}

public sealed record ResolvedSkill(string Name, SkillTier Tier, string Directory);

public sealed record RejectedSkill(string Name, SkillTier Tier, string Reason);

/// <summary>
/// What the runtime loads for one agent. <see cref="Directories"/> feeds <c>SkillDirectories</c> in tier order;
/// <see cref="Disabled"/> feeds <c>DisabledSkills</c>, so shadowed, unpinned or blocked skills are never offered.
/// </summary>
public sealed record SkillResolution(
    IReadOnlyList<string> Directories,
    IReadOnlyList<ResolvedSkill> Active,
    IReadOnlyList<string> Disabled,
    IReadOnlyList<RejectedSkill> Rejected);

/// <summary>
/// Resolves skills in tiers (FR-SKL-01): built-in (shipped with the binary, trusted), then the shared library
/// (<c>config/skills</c>), then per-agent (<c>config/agents/{id}/skills</c>). A later tier overrides an earlier one by
/// name. Shared and per-agent skills must match their lock-file pin and pass the static scan (FR-SKL-04).
/// </summary>
public sealed class SkillResolver(string builtInDirectory, string configRoot)
{
    public SkillResolution Resolve(AgentId agent)
    {
        var tiers = new (SkillTier Tier, string Directory)[]
        {
            (SkillTier.BuiltIn, builtInDirectory),
            (SkillTier.Shared, Path.Combine(configRoot, "skills")),
            (SkillTier.Agent, Path.Combine(configRoot, "agents", agent.Value, "skills")),
        };

        var active = new Dictionary<string, ResolvedSkill>(StringComparer.Ordinal);
        var disabled = new SortedSet<string>(StringComparer.Ordinal);
        var rejected = new List<RejectedSkill>();
        var directories = new List<string>();

        foreach (var (tier, directory) in tiers)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            directories.Add(directory);
            var pins = tier == SkillTier.BuiltIn ? null : SkillLock.Read(directory);
            foreach (var skillDir in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(skillDir);
                if (name.StartsWith('.') || name.StartsWith('_') || !File.Exists(Path.Combine(skillDir, "SKILL.md")))
                {
                    continue;
                }

                var reason = pins is null ? null : Verify(skillDir, name, pins);
                if (reason is not null)
                {
                    rejected.Add(new RejectedSkill(name, tier, reason));
                    disabled.Add(name);
                    continue;
                }

                if (active.TryGetValue(name, out var shadowed))
                {
                    // Shadowed by a higher tier; the earlier copy is reported so the override is visible.
                    rejected.Add(new RejectedSkill(name, shadowed.Tier, $"overridden by the {tier} tier"));
                }

                active[name] = new ResolvedSkill(name, tier, skillDir);
                disabled.Remove(name);
            }
        }

        return new SkillResolution(directories, [.. active.Values.OrderBy(s => s.Name, StringComparer.Ordinal)], [.. disabled], rejected);
    }

    private static string? Verify(string skillDir, string name, IReadOnlyDictionary<string, string> pins)
    {
        if (!pins.TryGetValue(name, out var pinned))
        {
            return "not pinned in skills.lock.json";
        }

        if (!string.Equals(SkillLock.Hash(skillDir), pinned, StringComparison.Ordinal))
        {
            return "content does not match its pin";
        }

        var findings = SkillScanner.ScanDirectory(skillDir);
        return SkillScanner.Blocks(findings)
            ? "static scan: " + string.Join("; ", findings.Where(f => f.Severity == SkillFindingSeverity.Block).Select(f => f.Message))
            : null;
    }
}
