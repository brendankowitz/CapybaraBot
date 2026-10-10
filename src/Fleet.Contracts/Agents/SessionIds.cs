namespace Fleet.Contracts.Agents;

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

    public static bool IsMain(string sessionId) => sessionId.EndsWith(":main", StringComparison.Ordinal);
}
