using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Enforces FR-SKL-04 at session build time: a stdio MCP server is attached only if its vendored executable still
/// matches the SHA-256 pinned in config, and, when the executable is an interpreter, the script it runs matches its own
/// pin too. Files are re-hashed on every build on purpose: a size/mtime cache would let a swapped file with a preserved
/// timestamp through.
/// </summary>
internal static partial class McpServerPins
{
    /// <summary>Interpreter subcommands that precede the script path, e.g. <c>deno run x.ts</c>, <c>dotnet exec x.dll</c>.</summary>
    private static readonly string[] Subcommands = ["run", "exec"];

    /// <returns>The verified executable path.</returns>
    /// <exception cref="InvalidOperationException">An executable or script is missing, unpinned or does not match its pin.</exception>
    public static string Verify(AgentId agent, McpServerSpec server)
    {
        var name = $"MCP server '{server.Name}' of {agent}";
        if (server.Command is not { Length: > 0 } command || server.Sha256 is not { Length: > 0 } pinned)
        {
            throw new InvalidOperationException($"{name} has no pinned executable.");
        }

        VerifyFile(name, command, pinned);

        if (IsInterpreter(command))
        {
            // The interpreter is usually a system binary; the vendored code is the script, so it needs its own pin.
            var script = ScriptArgument(server.Args)
                ?? throw new InvalidOperationException($"{name} runs an interpreter without a script path that can be pinned.");
            if (!Path.IsPathFullyQualified(script))
            {
                throw new InvalidOperationException($"{name}: script '{script}' must be an absolute path.");
            }

            var scriptPin = server.ScriptSha256
                ?? throw new InvalidOperationException($"{name} runs an interpreter but has no scriptSha256 pin for '{script}'.");
            VerifyFile(name, script, scriptPin);
        }

        return command;
    }

    internal static bool IsInterpreter(string command) => InterpreterName().IsMatch(Path.GetFileName(command));

    /// <summary>The first positional argument after any options and a leading <c>run</c>/<c>exec</c> subcommand.</summary>
    /// <remarks>
    /// An option whose value is a separate argument (<c>--import loader.mjs</c>) would be taken for the script. That fails
    /// closed: the value is not the pinned script, so its hash does not match. Config should use <c>--name=value</c>.
    /// </remarks>
    internal static string? ScriptArgument(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith('-') || Subcommands.Contains(arg, StringComparer.Ordinal))
            {
                continue;
            }

            return arg;
        }

        return null;
    }

    private static void VerifyFile(string name, string path, string pinned)
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

    /// <summary><c>node</c>, <c>python</c>, <c>python3</c>, <c>python3.12</c>, <c>deno</c>, <c>bun</c>, <c>dotnet</c>, with an optional <c>.exe</c>.</summary>
    [GeneratedRegex(@"^(?:node|python(?:3(?:\.\d+)?)?|deno|bun|dotnet)(?:\.exe)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InterpreterName();
}
