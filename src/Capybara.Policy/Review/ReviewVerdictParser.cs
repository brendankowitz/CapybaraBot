using System.Text;
using System.Text.RegularExpressions;

namespace Capybara.Policy.Review;

/// <summary>
/// Maps the reviewer model's text to a <see cref="ReviewResult"/> (FR-SAF-05). Anything that is not exactly one clear
/// verdict, including empty text, no verdict line or contradictory verdict lines, maps to
/// <see cref="ReviewVerdict.Escalate"/> so a confused or manipulated reviewer can only ever lead to a human decision.
/// </summary>
public static partial class ReviewVerdictParser
{
    public const int MaxRationaleLength = 300;

    public static ReviewResult Parse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return Escalate("reviewer returned no output");
        }

        var verdicts = new HashSet<ReviewVerdict>();
        string? rationale = null;
        foreach (var line in modelText.Split('\n'))
        {
            var verdict = VerdictLine().Match(line);
            if (verdict.Success)
            {
                verdicts.Add(Enum.Parse<ReviewVerdict>(verdict.Groups["v"].Value, ignoreCase: true));
                continue;
            }

            var reason = RationaleLine().Match(line);
            if (reason.Success && rationale is null)
            {
                rationale = Clean(reason.Groups["r"].Value);
            }
        }

        return verdicts.Count switch
        {
            0 => Escalate("reviewer output had no verdict"),
            1 => new ReviewResult(verdicts.Single(), string.IsNullOrEmpty(rationale) ? "reviewer gave no rationale" : rationale),
            _ => Escalate("reviewer output had conflicting verdicts"),
        };
    }

    private static ReviewResult Escalate(string reason) => new(ReviewVerdict.Escalate, reason);

    /// <summary>Single-line, control-free and length-capped, since it is shown in approval cards and audit detail.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length <= MaxRationaleLength ? cleaned : string.Concat(cleaned.AsSpan(0, MaxRationaleLength), "…");
    }

    [GeneratedRegex(@"^[\s*`>#_-]*VERDICT[\s*`_]*:[\s*`_]*(?<v>ALLOW|ESCALATE|BLOCK)[\s*`_.]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerdictLine();

    [GeneratedRegex(@"^[\s*`>#_-]*RATIONALE[\s*`_]*:[\s*`_]*(?<r>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RationaleLine();
}
