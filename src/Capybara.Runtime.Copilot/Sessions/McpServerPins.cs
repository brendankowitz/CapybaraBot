using System.Security.Cryptography;
using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Enforces FR-SKL-04 at session build time: a stdio MCP server is attached only if its vendored executable still
/// matches the SHA-256 pinned in config. The file is re-hashed on every build on purpose: a size/mtime cache would let a
/// swapped binary with a preserved timestamp through.
/// </summary>
internal static class McpServerPins
{
    /// <returns>The verified executable path.</returns>
    /// <exception cref="InvalidOperationException">The executable is missing or does not match its pin.</exception>
    public static string Verify(AgentId agent, McpServerSpec server)
    {
        if (server.Command is not { Length: > 0 } command || server.Sha256 is not { Length: > 0 } pinned)
        {
            throw new InvalidOperationException($"MCP server '{server.Name}' of {agent} has no pinned executable.");
        }

        if (!File.Exists(command))
        {
            throw new InvalidOperationException($"MCP server '{server.Name}' of {agent}: executable '{command}' does not exist.");
        }

        using var stream = File.OpenRead(command);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MCP server '{server.Name}' of {agent} does not match its pinned SHA-256; refusing to attach it.");
        }

        return command;
    }
}
