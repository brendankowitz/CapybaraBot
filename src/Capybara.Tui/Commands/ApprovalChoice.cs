using Capybara.Client;

namespace Capybara.Tui.Commands;

/// <summary>Maps the words a person types for an approval choice onto the wire values (<see cref="ApprovalDecisions"/>).</summary>
public static class ApprovalChoice
{
    /// <exception cref="ArgumentException">The text is not a known choice.</exception>
    public static string Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "ALLOWONCE" or "ALLOW" => ApprovalDecisions.AllowOnce,
            "ALWAYSALLOW" or "ALWAYS" => ApprovalDecisions.AlwaysAllow,
            "DENY" => ApprovalDecisions.Deny,
            _ => throw new ArgumentException($"Unknown approval choice '{text}'. Use allow-once, always or deny."),
        };
    }
}
