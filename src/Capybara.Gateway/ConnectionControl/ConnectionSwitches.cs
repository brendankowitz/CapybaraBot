using Capybara.Policy.Rules;

namespace Capybara.Gateway.ConnectionControl;

/// <summary>
/// Turning a connection off from the desktop (FR-CON-07). A disabled connection is a deny rule on all of its tools
/// (<c>{connection}_*</c>) for every agent: the policy gate enforces it on each call, including in sessions that are
/// already running, and it is kept with the learned rules so it survives a restart. Connection ids never contain
/// <c>_</c>, so the pattern matches exactly that connection's tools.
/// </summary>
/// <remarks>Thread-safe: changes are serialized.</remarks>
public sealed class ConnectionSwitches(RulesEngine rules, LearnedRuleStore store)
{
    private const string ReasonPrefix = "connection disabled";

    private readonly Lock _gate = new();

    public bool IsEnabled(string connection) => !rules.LearnedRules.Any(rule => IsSwitch(rule, connection));

    /// <summary>Returns whether anything changed.</summary>
    public bool Set(string connection, bool enabled, string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        lock (_gate)
        {
            if (enabled)
            {
                if (rules.RemoveLearned(rule => IsSwitch(rule, connection)) == 0)
                {
                    return false;
                }

                store.Save(rules.LearnedRules);
                return true;
            }

            if (!IsEnabled(connection))
            {
                return false;
            }

            // Learned rules are saved as they are added.
            rules.Add(new PolicyRule { Effect = RuleEffect.Deny, Agent = "*", Tool = ToolPattern(connection), Reason = $"{ReasonPrefix} by {by}" });
            return true;
        }
    }

    private static string ToolPattern(string connection) => $"{connection}_*";

    private static bool IsSwitch(PolicyRule rule, string connection) =>
        rule.Effect == RuleEffect.Deny
        && rule.Agent == "*"
        && rule.Tool == ToolPattern(connection)
        && rule.Reason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) == true;
}
