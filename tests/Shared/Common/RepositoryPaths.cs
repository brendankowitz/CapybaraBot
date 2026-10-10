namespace Fleet.Testing;

internal static class RepositoryPaths
{
    /// <summary>The repository root, found by walking up to <c>global.json</c>.</summary>
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static string SampleConfig => Path.Combine(Root, "config");
}
