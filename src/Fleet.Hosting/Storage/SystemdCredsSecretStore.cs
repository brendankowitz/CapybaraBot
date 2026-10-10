using System.Text.RegularExpressions;
using Fleet.Contracts.Storage;
using Fleet.Hosting.Agents;

namespace Fleet.Hosting.Storage;

/// <summary>Secret store backed by <c>systemd-creds</c> encrypted credentials and <c>$CREDENTIALS_DIRECTORY</c> reads (FR-HST-02, FR-HST-06).</summary>
public sealed partial class SystemdCredsSecretStore(string encryptedCredentialsDirectory, ICommandRunner runner, string? credentialsDirectory = null) : ISecretStore
{
    private readonly string _encryptedCredentialsDirectory = encryptedCredentialsDirectory;
    private readonly ICommandRunner _runner = runner;
    private readonly string _credentialsDirectory = credentialsDirectory ?? Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY") ?? string.Empty;

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (string.IsNullOrEmpty(_credentialsDirectory))
        {
            return null;
        }

        var path = Path.Combine(_credentialsDirectory, name);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);
        Directory.CreateDirectory(_encryptedCredentialsDirectory);
        var output = Path.Combine(_encryptedCredentialsDirectory, name + ".cred");
        var result = await _runner.RunAsync(new CommandRunRequest("systemd-creds", ["encrypt", $"--name={name}", "-", output], StandardInput: value), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemd-creds failed: {result.StandardError}");
        }
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var path = Path.Combine(_encryptedCredentialsDirectory, name + ".cred");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_encryptedCredentialsDirectory))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        var names = Directory.EnumerateFiles(_encryptedCredentialsDirectory, "*.cred")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => name is not null && NameRegex().IsMatch(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<string>>(names);
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException("Secret names must match [a-z0-9-_.]+.", nameof(name));
        }
    }

    [GeneratedRegex("^[a-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
