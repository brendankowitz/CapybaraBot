using Azure.Security.KeyVault.Secrets;
using Fleet.Contracts.Storage;

namespace Fleet.Hosting.Storage;

/// <summary>Azure Key Vault implementation of fleet secret storage (FR-HST-06, FR-HST-07).</summary>
public sealed class KeyVaultSecretStore(SecretClient client) : ISecretStore
{
    private readonly SecretClient _client = client;

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        SystemdCredsSecretStore.ValidateName(name);
        try
        {
            var response = await _client.GetSecretAsync(name, cancellationToken: cancellationToken);
            return response.Value.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        SystemdCredsSecretStore.ValidateName(name);
        await _client.SetSecretAsync(name, value, cancellationToken);
    }

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        SystemdCredsSecretStore.ValidateName(name);
        await _client.StartDeleteSecretAsync(name, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        await foreach (var property in _client.GetPropertiesOfSecretsAsync(cancellationToken))
        {
            names.Add(property.Name);
        }

        return names;
    }
}
