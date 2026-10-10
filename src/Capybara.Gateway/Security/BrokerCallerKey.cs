using System.Security.Cryptography;
using System.Text;

namespace Capybara.Gateway.Security;

/// <summary>
/// The shared broker API key, as the one credential that may post consent requests (FR-CON-08). Only its SHA-256 is
/// kept; with no key provisioned every broker call is refused.
/// </summary>
public sealed class BrokerCallerKey
{
    private readonly byte[]? _hash;

    public BrokerCallerKey(string? key) => _hash = string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Whether <paramref name="authorization"/> is <c>Bearer {key}</c>. Constant time in the key.</summary>
    public bool Matches(string authorization)
    {
        const string BearerPrefix = "Bearer ";
        if (_hash is null || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(authorization[BearerPrefix.Length..].Trim()), presented);
        return CryptographicOperations.FixedTimeEquals(presented, _hash);
    }
}
