using System.Text.RegularExpressions;

namespace Fleet.Memory.Safety;

/// <summary>Rejects writes that contain secrets or categories on the sensitive-data denylist (FR-MEM-06, FR-SAF-08).</summary>
public static partial class SensitiveDataFilter
{
    public static string? FindViolation(string content)
    {
        var match = Pattern().Match(content);
        return match.Success ? Describe(match) : null;
    }

    private static string Describe(Match match) =>
        match.Groups["key"].Success ? "private key" :
        match.Groups["token"].Success ? "access token" :
        match.Groups["card"].Success ? "payment card number" :
        match.Groups["ssn"].Success ? "national id number" :
        "credential";

    [GeneratedRegex(
        @"(?<key>-----BEGIN [A-Z ]*PRIVATE KEY-----)|(?<token>\b(ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}|\bxox[abp]-[A-Za-z0-9-]{10,}|\bsk-[A-Za-z0-9]{20,}|\bAKIA[0-9A-Z]{16}\b)|(?<card>\b(?:\d[ -]?){13,16}\b)|(?<ssn>\b\d{3}-\d{2}-\d{4}\b)|(?<cred>\b(password|passwd|pwd)\s*[:=]\s*\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();
}
