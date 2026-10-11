namespace Capybara.Tui.Commands;

public static class TokenFile
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "capybara", "token");

    public static string ReadToken(string? path = null)
    {
        var env = Environment.GetEnvironmentVariable("CAPYBARA_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var tokenPath = path ?? DefaultPath;
        EnsurePrivate(tokenPath);
        return File.ReadAllText(tokenPath).Trim();
    }

    public static void EnsurePrivate(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Token file was not found.", path);
        }

#pragma warning disable CA1416 // capybarad and the TUI target the Linux host described in FR-CLI-05.
        var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0)
        {
            throw new UnauthorizedAccessException("Refusing a token file readable by group or other users. Use chmod 600.");
        }
    }
}
