namespace Fleet.Contracts.Storage;

/// <summary>
/// Encrypted-at-rest secret storage (FR-CON-04, FR-HST-06): <c>systemd-creds</c> locally, Key Vault on Azure.
/// Only the connection broker reads secrets; values never enter model context (FR-SAF-08).
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Names only, never values.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
}
