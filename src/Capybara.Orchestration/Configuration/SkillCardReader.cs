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

        /// <summary>
        /// The Agent Skills standard spells this <c>allowed-tools</c> and allows a list or a comma/space-separated string;
        /// <c>allowedTools</c> is accepted too. Read only to warn that it grants nothing (FR-SKL-03).
        /// </summary>
        [YamlMember(Alias = "allowed-tools", ApplyNamingConventions = false)]
        public object? AllowedToolsKebab { get; set; }

        public object? AllowedTools { get; set; }

        public bool DeclaresAllowedTools => IsDeclared(AllowedToolsKebab) || IsDeclared(AllowedTools);

        private static bool IsDeclared(object? value) => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            System.Collections.ICollection items => items.Count > 0,
            _ => true,
        };
    }
#pragma warning restore CA1812, CA2227, CA1002
}
