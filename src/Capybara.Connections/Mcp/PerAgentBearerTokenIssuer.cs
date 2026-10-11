using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Capybara.Connections.Mcp;

/// <summary>
/// Random per-agent bearer tokens for the loopback MCP endpoint (FR-CON-03). Tokens live only in broker memory: a
/// broker restart invalidates them, and capybarad fetches a fresh one when it creates or resumes a session (FR-AGT-03).
/// </summary>
/// <remarks>Thread-safe. One token per agent, shared by that agent's sessions until <see cref="Revoke"/>.</remarks>
public sealed class PerAgentBearerTokenIssuer
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public string GetOrIssue(string agentId) =>
        _tokens.GetOrAdd(agentId, static _ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public void Revoke(string agentId) => _tokens.TryRemove(agentId, out _);

    public bool Validate(string agentId, string? token) =>
        token is not null
        && _tokens.TryGetValue(agentId, out var expected)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
}
