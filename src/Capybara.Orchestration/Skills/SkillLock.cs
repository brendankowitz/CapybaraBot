using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Capybara.Orchestration.Skills;

/// <summary>
/// <c>skills.lock.json</c> pins each skill folder in a tier to a content hash (FR-SKL-04). A skill whose files no longer
/// match its pin, or that has no pin, is not loaded. Only human actions (approving a skill proposal, or the lock
/// endpoint) write the lock.
/// </summary>
public static class SkillLock
{
    public const string FileName = "skills.lock.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyDictionary<string, string> Read(string tierDirectory)
    {
        var file = Path.Combine(tierDirectory, FileName);
        return File.Exists(file)
            ? JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(file), Json) ?? []
            : new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Pins (or re-pins) one skill folder in its tier's lock file.</summary>
    public static string Pin(string tierDirectory, string skillName)
    {
        var hash = Hash(Path.Combine(tierDirectory, skillName));
        var entries = new SortedDictionary<string, string>(Read(tierDirectory).ToDictionary(), StringComparer.Ordinal) { [skillName] = hash };
        Write(tierDirectory, entries);
        return hash;
    }

    public static void Unpin(string tierDirectory, string skillName)
    {
        var entries = new SortedDictionary<string, string>(Read(tierDirectory).ToDictionary(), StringComparer.Ordinal);
        if (entries.Remove(skillName))
        {
            Write(tierDirectory, entries);
        }
    }

    /// <summary>SHA-256 over every file's relative path and bytes, in ordinal path order.</summary>
    public static string Hash(string skillDirectory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories)
                     .Select(f => (Full: f, Relative: Path.GetRelativePath(skillDirectory, f).Replace('\\', '/')))
                     .OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\n"));
            sha.AppendData(File.ReadAllBytes(file.Full));
            sha.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static void Write(string tierDirectory, SortedDictionary<string, string> entries)
    {
        var file = Path.Combine(tierDirectory, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
        File.Move(temp, file, overwrite: true);
    }
}
