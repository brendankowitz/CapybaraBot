namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Validates <c>egress:</c> entries (FR-SAF-07, NFR-01). Entries reach sandbox policies and systemd unit files, so
/// only plain lowercase ASCII hostnames are accepted: two or more labels of <c>[a-z0-9-]</c> (1–63 characters, no
/// leading or trailing hyphen, the last label not all digits), at most 253 characters, optionally prefixed by
/// <c>*.</c>. No whitespace, controls, IP addresses, CIDR ranges, ports or look-alike Unicode. Upper case is folded.
/// </summary>
/// <remarks>
/// <see cref="Unrestricted"/> (<c>*</c>) is the one exception: a human may grant an agent with no private-data leg open
/// web access (the sample <c>research</c> agent), but only as the sole entry, and a proposal can never introduce it
/// (<c>ProposalScope</c>).
/// </remarks>
internal static class EgressDomain
{
    public const string Unrestricted = "*";

    private const int MaxLength = 253;

    /// <summary>Validated, lower-cased entries; problems are appended to <paramref name="errors"/>.</summary>
    public static IReadOnlyList<string> Normalize(string agent, IReadOnlyList<string?> entries, List<string> errors)
    {
        var normalized = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry == Unrestricted)
            {
                if (entries.Count != 1)
                {
                    errors.Add($"{agent}: egress '*' (unrestricted) must be the only entry.");
                }

                normalized.Add(Unrestricted);
            }
            else if (TryNormalize(entry, out var host))
            {
                if (!normalized.Contains(host, StringComparer.Ordinal))
                {
                    normalized.Add(host);
                }
            }
            else
            {
                errors.Add($"{agent}: egress entry '{ReviewableText.Escape(Clip(entry ?? string.Empty))}' is not a hostname (lower-case ASCII labels, optional '*.' prefix, no IPs, ports or spaces).");
            }
        }

        return normalized;
    }

    public static bool TryNormalize(string? entry, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrEmpty(entry) || entry.Length > MaxLength || !entry.All(char.IsAscii))
        {
            // ASCII is checked before case folding, so a look-alike such as KELVIN SIGN cannot fold into 'k'.
            return false;
        }

        var lowered = entry.ToLowerInvariant();
        var name = lowered.StartsWith("*.", StringComparison.Ordinal) ? lowered[2..] : lowered;
        var labels = name.Split('.');
        if (labels.Length < 2 || !labels.All(IsLabel) || labels[^1].All(char.IsAsciiDigit))
        {
            return false;
        }

        host = lowered;
        return true;
    }

    private static bool IsLabel(string label) =>
        label.Length is >= 1 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static string Clip(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
