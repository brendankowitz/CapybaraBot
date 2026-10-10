using System.Text.RegularExpressions;

namespace Capybara.Memory.Safety;

/// <summary>
/// Heuristic scan for prompt-injection markers when memory is loaded (FR-MEM-10). Detection is not a defense on its own;
/// it only quarantines obviously hostile pages. The structural defenses are scopes, taint and the policy gate.
/// </summary>
public static partial class InjectionScanner
{
    public static bool LooksInjected(string content) => Pattern().IsMatch(content);

    [GeneratedRegex(
        @"ignore\s+(all\s+|any\s+)?(previous|prior|above)\s+(instructions|rules)|disregard\s+(your|all|the)\s+(instructions|rules|system)|you\s+are\s+now\s+|new\s+system\s+prompt|<\|im_start\|>|\[\[?SYSTEM\]\]?|BEGIN\s+(SYSTEM|ADMIN)\s+(PROMPT|INSTRUCTIONS)|(add|create|schedule)\s+(a\s+)?(cron|scheduled\s+task|routine|webhook|telegram)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();
}
