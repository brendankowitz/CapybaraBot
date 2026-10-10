using System.Text.RegularExpressions;
using Fleet.Orchestration.Configuration;

namespace Fleet.Orchestration.Skills;

public enum SkillFindingSeverity
{
    /// <summary>Shown to the reviewer; does not block.</summary>
    Warning,

    /// <summary>The skill cannot be activated.</summary>
    Block,
}

public sealed record SkillFinding(SkillFindingSeverity Severity, string File, string Message);

/// <summary>
/// Static scan for skills (FR-SKL-04) and template conformance (FR-SKL-05). It is a tripwire for obvious installers,
/// exfiltration and hidden instructions, not a sandbox: skills never grant permissions (FR-SKL-03).
/// </summary>
public static partial class SkillScanner
{
    public const long MaxFileBytes = 256 * 1024;

    private static readonly string[] TemplateSections = ["When to use", "Inputs", "Steps", "Validation", "Outputs", "Approval boundary"];

    private static readonly (Regex Pattern, string Message)[] BlockPatterns =
    [
        (PipeToShell(), "downloads piped into a shell"),
        (PackageInstall(), "package-manager install (install hooks are not allowed; vendor the dependency)"),
        (SecretPaths(), "reads credential locations"),
        (InjectionPhrases(), "prompt-injection phrasing"),
        (EncodedExec(), "decodes and executes an encoded payload"),
    ];

    /// <summary>Scans every file in a skill folder.</summary>
    public static IReadOnlyList<SkillFinding> ScanDirectory(string skillDirectory)
    {
        var findings = new List<SkillFinding>();
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            findings.Add(new SkillFinding(SkillFindingSeverity.Block, "SKILL.md", "missing SKILL.md"));
        }

        foreach (var file in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(skillDirectory, file);
            var info = new FileInfo(file);
            if (info.LinkTarget is not null)
            {
                findings.Add(new SkillFinding(SkillFindingSeverity.Block, relative, "symbolic links are not allowed"));
                continue;
            }

            if (info.Length > MaxFileBytes)
            {
                findings.Add(new SkillFinding(SkillFindingSeverity.Block, relative, "file too large to review"));
                continue;
            }

            findings.AddRange(relative == "SKILL.md" ? ScanSkill(File.ReadAllText(file)) : ScanText(relative, File.ReadAllText(file)));
        }

        return findings;
    }

    /// <summary>Scans a SKILL.md body, including frontmatter and template checks.</summary>
    public static IReadOnlyList<SkillFinding> ScanSkill(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var findings = new List<SkillFinding>(ScanText("SKILL.md", content));
        if (!SkillCardReader.TryReadFrontmatter(content, out var front) || string.IsNullOrWhiteSpace(front.Name) || string.IsNullOrWhiteSpace(front.Description))
        {
            findings.Add(new SkillFinding(SkillFindingSeverity.Block, "SKILL.md", "frontmatter must declare name and description"));
        }
        else if (front.AllowedTools.Count > 0)
        {
            findings.Add(new SkillFinding(SkillFindingSeverity.Warning, "SKILL.md", "allowed-tools grants nothing; the agent's grants still apply"));
        }

        foreach (var section in TemplateSections)
        {
            if (!content.Contains("# " + section, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SkillFinding(SkillFindingSeverity.Warning, "SKILL.md", $"template section '{section}' is missing"));
            }
        }

        return findings;
    }

    public static bool Blocks(IEnumerable<SkillFinding> findings) => findings.Any(f => f.Severity == SkillFindingSeverity.Block);

    private static IEnumerable<SkillFinding> ScanText(string file, string content)
    {
        foreach (var (pattern, message) in BlockPatterns)
        {
            if (pattern.IsMatch(content))
            {
                yield return new SkillFinding(SkillFindingSeverity.Block, file, message);
            }
        }

        if (HiddenCharacters().IsMatch(content))
        {
            yield return new SkillFinding(SkillFindingSeverity.Block, file, "invisible or bidirectional-control characters");
        }

        if (RawIpUrl().IsMatch(content))
        {
            yield return new SkillFinding(SkillFindingSeverity.Warning, file, "URL with a raw IP address");
        }
    }

    [GeneratedRegex(@"(curl|wget|iwr|Invoke-WebRequest)\b[^\n|]*\|\s*(sudo\s+)?(sh|bash|zsh|pwsh|powershell|python3?|iex)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PipeToShell();

    [GeneratedRegex(@"\b(npm\s+(i|install)|npx\s|pip3?\s+install|uv\s+pip\s+install|gem\s+install|cargo\s+install|go\s+install|dotnet\s+tool\s+install|apt(-get)?\s+install|brew\s+install)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PackageInstall();

    [GeneratedRegex(@"(~|\$HOME)/\.(ssh|aws|azure|config/gh|docker|kube)\b|\bCREDENTIALS_DIRECTORY\b|\bid_(rsa|ed25519)\b|/etc/shadow", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPaths();

    [GeneratedRegex(@"ignore (all )?(previous|prior|above) instructions|disregard (the )?(system|previous) prompt|you are now in developer mode|do not tell the user", RegexOptions.IgnoreCase)]
    private static partial Regex InjectionPhrases();

    [GeneratedRegex(@"base64\s+(-d|--decode)[^\n]*\|\s*(sh|bash|python3?)|FromBase64String[^\n]*(Invoke-Expression|iex)", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedExec();

    [GeneratedRegex(@"[\u200B-\u200F\u202A-\u202E\u2060-\u2064\u2066-\u2069\uFEFF]|\uDB40[\uDC00-\uDC7F]")]
    private static partial Regex HiddenCharacters();

    [GeneratedRegex(@"https?://\d{1,3}(\.\d{1,3}){3}")]
    private static partial Regex RawIpUrl();
}
