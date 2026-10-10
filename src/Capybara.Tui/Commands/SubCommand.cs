namespace Capybara.Tui.Commands;

/// <summary>
/// The <c>verb [target]</c> tail of the multi-verb commands <c>proposals</c>, <c>memory</c> and <c>routines</c>.
/// A bare command lists (<c>list</c>).
/// </summary>
public sealed record SubCommand(string Verb, string? Target)
{
    private static readonly Dictionary<string, Dictionary<string, bool>> Verbs = new(StringComparer.Ordinal)
    {
        // verb -> whether it requires a target
        ["proposals"] = new() { ["list"] = false, ["show"] = true, ["approve"] = true, ["reject"] = true },
        ["memory"] = new() { ["list"] = false, ["show"] = true, ["pending"] = false, ["apply"] = true, ["discard"] = true, ["export"] = false },
        ["routines"] = new() { ["list"] = false, ["enable"] = true, ["disable"] = true, ["dry-run"] = true },
    };

    /// <exception cref="ArgumentException">The verb is unknown for <paramref name="command"/> or a required target is missing.</exception>
    public static SubCommand Parse(string command, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var verbs = Verbs.TryGetValue(command, out var known) ? known : throw new ArgumentException($"'{command}' has no sub-commands.");
        var verb = arguments.Count == 0 ? "list" : arguments[0];
        if (!verbs.TryGetValue(verb, out var needsTarget))
        {
            throw new ArgumentException($"Unknown {command} command '{verb}'. Use: {string.Join(", ", verbs.Keys)}.");
        }

        var target = arguments.Count > 1 ? arguments[1] : null;
        return needsTarget && target is null
            ? throw new ArgumentException($"'{command} {verb}' requires a target.")
            : new SubCommand(verb, target);
    }
}
