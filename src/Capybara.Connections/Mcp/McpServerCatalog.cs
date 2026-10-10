using System.Security.Cryptography;

namespace Capybara.Connections.Mcp;

/// <summary>
/// Vendored third-party MCP servers pinned by SHA-256 (FR-SKL-04). The broker verifies every pin at startup and
/// refuses to start on a mismatch or a missing file.
/// </summary>
public sealed class McpServerCatalog(IReadOnlyList<McpServerRegistration> servers)
{
    public IReadOnlyList<McpServerRegistration> Servers => servers;

    /// <exception cref="InvalidOperationException">The executable's hash does not match its pin.</exception>
    public static async ValueTask VerifyAsync(McpServerRegistration server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        await using var stream = File.OpenRead(server.ExecutablePath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, server.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MCP server hash mismatch for {server.Name}.");
        }
    }

    /// <summary>Returns one error per server that is missing or does not match its pin.</summary>
    public async ValueTask<IReadOnlyList<string>> VerifyAllAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var server in servers)
        {
            try
            {
                await VerifyAsync(server, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"MCP server '{server.Name}': {ex.Message}");
            }
        }

        return errors;
    }
}

/// <summary>A vendored MCP server executable and its pinned hash (FR-SKL-04).</summary>
public sealed class McpServerRegistration
{
    public string Name { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];
}
