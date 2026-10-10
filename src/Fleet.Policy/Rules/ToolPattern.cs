namespace Fleet.Policy.Rules;

/// <summary>
/// Matches source-qualified tool allowlist entries (FR-AGT-02): <c>custom:list_agents</c>, <c>mcp:workiq-ask</c>, or
/// a bare name. The runtime reports bare names, so the source prefix is stripped from both sides before globbing.
/// </summary>
public static class ToolPattern
{
    public const string CustomPrefix = "custom:";
    public const string McpPrefix = "mcp:";

    public static bool IsMatch(string pattern, string tool)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(tool);
        return Glob.IsMatch(pattern, tool) || Glob.IsMatch(Unqualify(pattern), Unqualify(tool));
    }

    /// <summary>Removes a <c>custom:</c> or <c>mcp:</c> source prefix.</summary>
    public static string Unqualify(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            return name[CustomPrefix.Length..];
        }

        return name.StartsWith(McpPrefix, StringComparison.Ordinal) ? name[McpPrefix.Length..] : name;
    }
}
