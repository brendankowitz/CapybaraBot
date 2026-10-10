using System.Security.Cryptography;
using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Enforces FR-SKL-04 at session build time. A stdio MCP server's command must match its pin, and unless the server is
/// declared <see cref="McpServerSpec.SelfContained"/>, the script it runs must match its own pin too. This is an
/// allowlist: there is no list of "known interpreters" to fall out of. Files are re-hashed on every build on purpose: a
/// size/mtime cache would let a swapped file with a preserved timestamp through.
/// </summary>
internal static class McpServerPins
{
    /// <summary>Subcommands that precede the script path, e.g. <c>deno run x.ts</c>, <c>dotnet exec x.dll</c>.</summary>
    private static readonly string[] Subcommands = ["run", "exec"];

    /// <returns>The verified executable path.</returns>
    /// <exception cref="InvalidOperationException">A file is missing, unpinned or does not match its pin.</exception>
    public static string Verify(AgentId agent, McpServerSpec server)
    {
        var name = $"MCP server '{server.Name}' of {agent}";
        if (server.Command is not { Length: > 0 } command || server.Sha256 is not { Length: > 0 } pinned)
        {
            throw new InvalidOperationException($"{name} has no pinned executable.");
        }

        VerifyFile(name, command, pinned);
        if (server.SelfContained)
        {
            return command;
        }

        var scriptPin = server.ScriptSha256
            ?? throw new InvalidOperationException($"{name} must pin the script it runs (scriptSha256) or be declared selfContained.");
        var script = ScriptArgument(server.Args)
            ?? throw new InvalidOperationException($"{name} has no script argument to check against its scriptSha256 pin.");
        if (!Path.IsPathFullyQualified(script))
        {
            throw new InvalidOperationException($"{name}: script '{script}' must be an absolute path.");
        }

        VerifyFile(name, script, scriptPin);
        return command;
    }

    /// <summary>The first positional argument after any options and a leading <c>run</c>/<c>exec</c> subcommand.</summary>
    /// <remarks>
    /// An option whose value is a separate argument (<c>--import loader.mjs</c>) would be taken for the script. That fails
    /// closed: the value is not the pinned script, so its hash does not match. Config should use <c>--name=value</c>.
    /// </remarks>
    internal static string? ScriptArgument(IReadOnlyList<string> args) =>
        args.FirstOrDefault(a => !a.StartsWith('-') && !Subcommands.Contains(a, StringComparer.Ordinal));

    /// <summary>Verifies one pinned file.</summary>
    /// <exception cref="InvalidOperationException">The file is missing or does not match.</exception>
    internal static void VerifyFile(string name, string path, string pinned)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{name}: '{path}' does not exist.");
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{name}: '{path}' does not match its pinned SHA-256; refusing to attach it.");
        }
    }
}
