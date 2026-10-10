namespace Capybara.Gateway.Composition;

/// <summary>
/// Reads systemd credentials (<c>LoadCredentialEncrypted=</c>) from <c>$CREDENTIALS_DIRECTORY</c>, so tokens and keys
/// never sit in config files or the environment (FR-HST-03).
/// </summary>
internal static class SystemdCredentials
{
    /// <summary>The credential file's path, or <see langword="null"/> outside systemd or for an invalid name.</summary>
    public static string? PathOf(string? name)
    {
        var directory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        return string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0
            ? null
            : Path.Combine(directory, name);
    }

    /// <summary>The trimmed credential, or <see langword="null"/> when it is not provided.</summary>
    public static string? Read(string? name) =>
        PathOf(name) is { } path && File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
