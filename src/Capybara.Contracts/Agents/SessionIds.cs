using System.Diagnostics.CodeAnalysis;

namespace Capybara.Contracts.Agents;

/// <summary>
/// Deterministic session id scheme. Resumable sessions require caller-supplied ids (FR-AGT-03).
/// </summary>
/// <remarks>
/// The Copilot CLI accepts only letters, digits, <c>-</c> and <c>_</c> in session ids (it rejects <c>:</c>, <c>.</c>,
/// <c>/</c> and the like), so parts are joined with <c>_</c>. Agent ids never contain <c>_</c>, so the agent and the
/// kind always parse unambiguously; a key may.
/// </remarks>
public static class SessionIds
{
    private const string Prefix = "agent";

    public static string Main(AgentId agent) => $"{Prefix}_{agent}_main";

    public static string Consult(AgentId agent, Guid consultId) => $"{Prefix}_{agent}_consult_{consultId:N}";

    public static string Task(AgentId agent, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return $"{Prefix}_{agent}_task_{taskId}";
    }

    /// <summary>An incognito conversation (FR-MEM-12): no memory is read or written in it.</summary>
    public static string Incognito(AgentId agent, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return $"{Prefix}_{agent}_incognito_{conversationId}";
    }

    /// <summary>Whether <paramref name="sessionId"/> uses only the characters the Copilot CLI accepts.</summary>
    public static bool IsValid(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsMain(string sessionId) =>
        Split(sessionId) is { Length: 3 } parts && parts[2] == "main";

    /// <summary>An ephemeral consult session (FR-A2A-02); it no longer exists once the consult returned.</summary>
    public static bool IsConsult(string? sessionId) =>
        Split(sessionId) is { Length: 4 } parts && parts[2] == "consult";

    /// <summary>Parses <c>agent_{agent}_task_{key}</c>; the key is a task id or a host-chosen isolated-session key.</summary>
    public static bool TryParseTask(string? sessionId, out AgentId agent, [NotNullWhen(true)] out string? key) =>
        TryParse(sessionId, "task", out agent, out key);

    /// <summary>Parses <c>agent_{agent}_incognito_{conversationId}</c>.</summary>
    public static bool TryParseIncognito(string? sessionId, out AgentId agent, [NotNullWhen(true)] out string? conversationId) =>
        TryParse(sessionId, "incognito", out agent, out conversationId);

    /// <summary>The agent a session id belongs to, for every scheme above.</summary>
    public static bool TryGetAgent(string? sessionId, out AgentId agent)
    {
        agent = default;
        return Split(sessionId) is { Length: >= 3 } parts && AgentId.TryParse(parts[1], out agent);
    }

    private static bool TryParse(string? sessionId, string kind, out AgentId agent, [NotNullWhen(true)] out string? key)
    {
        agent = default;
        key = null;
        if (Split(sessionId) is not { Length: 4 } parts || parts[2] != kind || parts[3].Length == 0 || !AgentId.TryParse(parts[1], out agent))
        {
            return false;
        }

        key = parts[3];
        return true;
    }

    /// <summary>Prefix, agent, kind and (optionally) the key, which may itself contain <c>_</c>.</summary>
    private static string[]? Split(string? sessionId)
    {
        var parts = sessionId?.Split('_', 4);
        return parts is { Length: >= 3 } && parts[0] == Prefix ? parts : null;
    }
}
