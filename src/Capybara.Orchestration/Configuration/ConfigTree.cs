using YamlDotNet.RepresentationModel;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Layout of the user-owned config tree and the primitive writes that human config changes are made of. Every path
/// is built from validated ids, never from free text.
/// </summary>
internal static class ConfigTree
{
    public const string RoutinesFile = "routines.yaml";

    public const string RulesFile = "rules.yaml";

    /// <summary>Staging folder for skill proposals inside an agent's skill tier (FR-SKL-03); ignored by the loader and git.</summary>
    public const string PendingSkillsFolder = "pending";

    public static string AgentsDirectory(string root) => Path.Combine(root, "agents");

    public static string AgentDirectory(string root, string agent) => Path.Combine(root, "agents", agent);

    public static string AgentYaml(string root, string agent) => Path.Combine(root, "agents", agent, "agent.yaml");

    public static string SkillsDirectory(string root, string agent) => Path.Combine(root, "agents", agent, "skills");

    public static string? ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// Config directories are <c>0750</c> and files <c>0640</c>, set explicitly so they never depend on capybarad's
    /// umask: capybara-broker runs as another user in the <c>capybara</c> group and must read every <c>agent.yaml</c>
    /// for its grants (FR-CON-02). Nothing in the tree is world-readable. No-ops on Windows.
    /// </summary>
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    /// <inheritdoc cref="DirectoryMode"/>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>Creates <paramref name="path"/> and any missing parents, giving each new directory <see cref="DirectoryMode"/>.</summary>
    public static void CreateDirectory(string path, UnixFileMode mode = DirectoryMode)
    {
        var missing = new Stack<string>();
        for (var dir = Path.GetFullPath(path); !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!)
        {
            missing.Push(dir);
        }

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            while (missing.TryPop(out var created))
            {
                File.SetUnixFileMode(created, mode);
            }
        }
    }

    /// <summary>Gives a file copied or moved into the tree <see cref="FileMode"/>.</summary>
    public static void SetFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }

    /// <summary>Writes through a flushed temp file and a rename, so readers never see a partial file, even after a crash.</summary>
    public static void WriteAtomic(string path, string content)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, System.IO.FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        SetFileMode(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Removes the <c>rules.yaml</c> entries scoped exactly to <paramref name="agent"/> and returns them.</summary>
    public static List<YamlMappingNode> RemoveRulesFor(string root, string agent)
    {
        var file = Path.Combine(root, RulesFile);
        var original = ReadOptional(file) ?? string.Empty;
        var rules = YamlEditor.ParseSequence(original);
        var scoped = rules.Children.OfType<YamlMappingNode>().Where(r => YamlEditor.GetScalar(r, "agent") == agent).ToList();
        if (scoped.Count > 0)
        {
            scoped.ForEach(r => rules.Children.Remove(r));
            WriteAtomic(file, YamlEditor.SerializeWithHeader(original, rules));
        }

        return scoped;
    }

    /// <summary>Reads <c>routines.yaml</c> (missing or empty is an empty sequence) and its original text.</summary>
    public static (string Original, YamlSequenceNode Routines) ReadRoutines(string root)
    {
        var original = ReadOptional(Path.Combine(root, RoutinesFile)) ?? string.Empty;
        return (original, YamlEditor.ParseSequence(original));
    }

    public static void WriteRoutines(string root, string original, YamlSequenceNode routines) =>
        WriteAtomic(Path.Combine(root, RoutinesFile), YamlEditor.SerializeWithHeader(original, routines));

    public static YamlMappingNode? FindRoutine(YamlSequenceNode routines, string id) =>
        routines.Children.OfType<YamlMappingNode>().FirstOrDefault(r => YamlEditor.GetScalar(r, "id") == id);

    public static IEnumerable<YamlMappingNode> RoutinesOwnedBy(YamlSequenceNode routines, string owner) =>
        routines.Children.OfType<YamlMappingNode>().Where(r => YamlEditor.GetScalar(r, "owner") == owner);
}
