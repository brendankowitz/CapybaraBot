using System.Diagnostics.CodeAnalysis;

namespace Capybara.Contracts.Agents;

/// <summary>
/// Deterministic session id scheme. Resumable sessions require caller-supplied ids (FR-AGT-03).
/// </summary>
public static class SessionIds
{
    public static string Main(AgentId agent) => $"agent:{agent}:main";

    public static string Consult(AgentId agent, Guid consultId) => $"agent:{agent}:consult:{consultId:N}";

    public static string Task(AgentId agent, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return $"agent:{agent}:task:{taskId}";
    }

    /// <summary>An incognito conversation (FR-MEM-12): no memory is read or written in it.</summary>
    public static string Incognito(AgentId agent, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return $"agent:{agent}:incognito:{conversationId}";
    }

    public static bool IsMain(string sessionId) => sessionId.EndsWith(":main", StringComparison.Ordinal);

    /// <summary>Parses <c>agent:{agent}:task:{key}</c>; the key is a task id or a host-chosen isolated-session key.</summary>
    public static bool TryParseTask(string? sessionId, out AgentId agent, [NotNullWhen(true)] out string? key) =>
        TryParse(sessionId, "task", out agent, out key);

    /// <summary>Parses <c>agent:{agent}:incognito:{conversationId}</c>.</summary>
    public static bool TryParseIncognito(string? sessionId, out AgentId agent, [NotNullWhen(true)] out string? conversationId) =>
        TryParse(sessionId, "incognito", out agent, out conversationId);

    /// <summary>The agent a session id belongs to, for every scheme above.</summary>
    public static bool TryGetAgent(string? sessionId, out AgentId agent)
    {
        agent = default;
        var parts = sessionId?.Split(':');
        return parts is { Length: >= 3 } && parts[0] == "agent" && AgentId.TryParse(parts[1], out agent);
    }

    private static bool TryParse(string? sessionId, string kind, out AgentId agent, [NotNullWhen(true)] out string? key)
    {
        agent = default;
        key = null;
        var parts = sessionId?.Split(':');
        if (parts is not { Length: 4 } || parts[0] != "agent" || parts[2] != kind || parts[3].Length == 0 || !AgentId.TryParse(parts[1], out agent))
        {
            return false;
        }

        key = parts[3];
        return true;
    }
}
