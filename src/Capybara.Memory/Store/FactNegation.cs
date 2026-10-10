using System.Text;

namespace Capybara.Memory.Store;

/// <summary>
/// Deterministic contradiction detection for the DELETE decision of <c>remember</c> (FR-MEM-03). A fact that negates an
/// existing bullet ("Alice no longer works at Acme" vs "- Alice works at Acme") retracts that bullet instead of adding
/// a contradicting one. Matching is conservative: the negation-stripped, lightly stemmed word sets must be equal.
/// </summary>
internal static class FactNegation
{
    private static readonly HashSet<string> Markers = new(StringComparer.Ordinal)
    {
        "no", "not", "never", "longer", "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent", "wont", "cant", "cannot", "stopped",
    };

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "does", "do", "did", "is", "are", "was", "were", "anymore", "any", "the", "a", "an", "that", "now",
    };

    /// <summary>
    /// Returns <paramref name="body"/> without the bullets that <paramref name="fact"/> negates, or <see langword="null"/>
    /// when the fact is not a negation or matches no bullet.
    /// </summary>
    public static string? Retract(string body, string fact)
    {
        var factWords = Words(fact);
        if (!factWords.Any(Markers.Contains))
        {
            return null;
        }

        var factCore = Core(factWords);
        if (factCore.Count == 0)
        {
            return null;
        }

        var lines = body.ReplaceLineEndings("\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var removed = false;
        foreach (var line in lines)
        {
            if (IsPositiveMatch(line, factCore))
            {
                removed = true;
                continue;
            }

            kept.Add(line);
        }

        return removed ? string.Join('\n', kept) : null;
    }

    private static bool IsPositiveMatch(string line, HashSet<string> factCore)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            return false;
        }

        var words = Words(trimmed[2..]);
        return !words.Any(Markers.Contains) && Core(words).SetEquals(factCore);
    }

    private static HashSet<string> Core(IEnumerable<string> words) =>
        [.. words.Where(w => !Markers.Contains(w) && !Fillers.Contains(w)).Select(Stem)];

    private static string Stem(string word) => word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '’')
            {
                // Contractions collapse: "doesn't" -> "doesnt".
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
