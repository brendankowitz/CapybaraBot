using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Capybara.Policy.Redaction;

/// <summary>
/// Removes secrets from anything headed for model context or logs (FR-SAF-08). Known secret values (registered by
/// the broker or the runtime when it handles them) are replaced verbatim; common token shapes are replaced by pattern.
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Placeholder = "[REDACTED]";

    private const int MinimumKnownSecretLength = 8;

    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);

    public SecretRedactor(IEnumerable<string>? knownSecrets = null)
    {
        foreach (var secret in knownSecrets ?? [])
        {
            AddKnownSecret(secret);
        }
    }

    /// <summary>Registers a literal secret value. Very short values are ignored to avoid shredding normal text.</summary>
    public void AddKnownSecret(string value)
    {
        if (!string.IsNullOrEmpty(value) && value.Length >= MinimumKnownSecretLength)
        {
            _known.TryAdd(value, 0);
        }
    }

    public string Redact(string text)
    {
        TryRedact(text, out var redacted);
        return redacted;
    }

    /// <summary>Returns <see langword="true"/> when anything was redacted.</summary>
    public bool TryRedact(string text, out string redacted)
    {
        if (string.IsNullOrEmpty(text))
        {
            redacted = text ?? string.Empty;
            return false;
        }

        var result = text;
        foreach (var secret in _known.Keys.OrderByDescending(s => s.Length))
        {
            result = result.Replace(secret, Placeholder, StringComparison.Ordinal);
        }

        result = PrivateKeyBlock().Replace(result, Placeholder);
        result = TokenPatterns().Replace(result, Placeholder);
        result = BearerHeader().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
        result = SecretAssignment().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Placeholder);

        redacted = result;
        return !ReferenceEquals(result, text) && !string.Equals(result, text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|ya29\.[A-Za-z0-9_\-]{20,}|AKIA[0-9A-Z]{16}|xox[abposr]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_\-]{20,}|eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPatterns();

    [GeneratedRegex(@"(?<prefix>\b(?:Bearer|token)\s+)[A-Za-z0-9._~+/\-]{16,}=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerHeader();

    [GeneratedRegex(
        @"(?<key>\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)""?)(?<sep>\s*[:=]\s*""?)[^\s""',;]{6,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretAssignment();
}
