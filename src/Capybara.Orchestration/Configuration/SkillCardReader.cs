using Capybara.Contracts.Agents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Reads the YAML frontmatter (<c>name</c>, <c>description</c>, <c>examples</c>) of each active <c>SKILL.md</c> under an
/// agent's skill directory for its card (FR-A2A-01). Staged skills under <c>pending/</c> are ignored (FR-SKL-03).
/// </summary>
internal static class SkillCardReader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<AgentSkill> Read(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
        {
            return [];
        }

        var skills = new List<AgentSkill>();
        foreach (var dir in Directory.EnumerateDirectories(skillsDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name is "pending" || name.StartsWith('.') || name.StartsWith('_'))
            {
                continue;
            }

            var file = Path.Combine(dir, "SKILL.md");
            if (File.Exists(file) && TryReadFrontmatter(File.ReadAllText(file), out var front))
            {
                skills.Add(new AgentSkill(front.Name ?? name, front.Description ?? string.Empty, front.Examples));
            }
        }

        return skills;
    }

    internal static bool TryReadFrontmatter(string text, out SkillFrontmatter frontmatter)
    {
        frontmatter = new SkillFrontmatter();
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        try
        {
            frontmatter = Yaml.Deserialize<SkillFrontmatter?>(text[3..end]) ?? new SkillFrontmatter();
            return true;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

#pragma warning disable CA1812, CA2227, CA1002 // Populated by YamlDotNet.
    internal sealed class SkillFrontmatter
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<string> Examples { get; set; } = [];

        public List<string> AllowedTools { get; set; } = [];
    }
#pragma warning restore CA1812, CA2227, CA1002
}
