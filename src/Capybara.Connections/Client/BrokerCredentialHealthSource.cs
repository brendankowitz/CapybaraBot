using Capybara.Connections.Health;

namespace Capybara.Connections.Client;

/// <summary>
/// Credential health read from the broker's vault metadata (FR-CON-05). The token health monitor runs in capybarad,
/// where alerts reach the user's channels; the broker only reports expiry metadata, never secrets.
/// </summary>
public sealed class BrokerCredentialHealthSource(BrokerClient client) : ICredentialHealthSource
{
    public ValueTask<IReadOnlyList<CredentialHealth>> ListAsync(CancellationToken cancellationToken = default) => client.ListCredentialHealthAsync(cancellationToken);
}
