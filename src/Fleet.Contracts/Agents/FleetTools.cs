namespace Fleet.Contracts.Agents;

/// <summary>Names of the host-provided (custom) tools, source-qualified as the SDK expects.</summary>
public static class FleetTools
{
    public const string ListAgents = "list_agents";
    public const string AskAgent = "ask_agent";
    public const string SendMessage = "send_message";
    public const string DelegateTask = "delegate_task";
    public const string ProposeChange = "propose_change";
    public const string MemoryList = "memory_list";
    public const string MemoryRead = "memory_read";
    public const string MemorySearch = "memory_search";
    public const string Remember = "remember";
    public const string Forget = "forget";

    /// <summary>Tools that let a session reach other agents.</summary>
    public static readonly IReadOnlyList<string> AgentToAgent = [AskAgent, SendMessage, DelegateTask];

    /// <summary>Tools that mutate memory; absent from consult and incognito sessions.</summary>
    public static readonly IReadOnlyList<string> MemoryWrite = [Remember, Forget];

    /// <summary>
    /// Sessions at maximum depth get no agent-to-agent tools at all — removed, not merely denied (FR-A2A-06).
    /// </summary>
    public static IReadOnlyList<string> ForDepth(IEnumerable<string> tools, int depth) =>
        depth >= Messaging.Envelope.MaxDepth
            ? [.. tools.Where(t => !AgentToAgent.Contains(t, StringComparer.Ordinal))]
            : [.. tools];

    /// <summary>Consult sessions are read-only and cannot fan out further than the envelope allows.</summary>
    public static IReadOnlyList<string> ForConsult(IEnumerable<string> tools, int depth) =>
        [.. ForDepth(tools, depth).Where(t => !MemoryWrite.Contains(t, StringComparer.Ordinal) && t != ProposeChange)];
}
