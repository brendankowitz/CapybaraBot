using Capybara.Contracts.Storage;

namespace Capybara.Connections.Vault;

/// <summary>Read-only secret store over systemd's decrypted <c>$CREDENTIALS_DIRECTORY</c> (FR-CON-04, FR-HST-02).</summary>
public sealed class CredentialsDirectorySecretStore(string credentialsDirectory) : ISecretStore
{
    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(credentialsDirectory, SecretNames.Require(name));
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, cancellationToken)).Trim() : null;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>(Directory.Exists(credentialsDirectory)
            ? [.. Directory.EnumerateFiles(credentialsDirectory).Select(Path.GetFileName).Where(SecretNames.IsValid).Select(n => n!)]
            : []);

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException("The credentials directory is read-only.");

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException("The credentials directory is read-only.");
}

/// <summary>
/// The broker's vault layout (FR-CON-04): secrets written at runtime (OAuth refreshes, consents) go to the writable
/// encrypted store and shadow the read-only values the admin seeded through systemd credentials.
/// </summary>
public sealed class LayeredSecretStore(ISecretStore writable, ISecretStore seed) : ISecretStore
{
    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        await writable.GetAsync(name, cancellationToken) ?? await seed.GetAsync(name, cancellationToken);

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => writable.SetAsync(name, value, cancellationToken);

    /// <summary>Deletes the writable copy only; a seeded value reappears until the admin removes the credential.</summary>
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => writable.DeleteAsync(name, cancellationToken);

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. (await writable.ListAsync(cancellationToken)).Union(await seed.ListAsync(cancellationToken), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
