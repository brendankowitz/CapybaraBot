namespace Capybara.Contracts.Agents;

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

    /// <summary>Marks a memory page as no longer current (FR-MEM-03).</summary>
    public const string Supersede = "supersede";

    /// <summary>Records that a remembered fact was re-verified against its source system (FR-MEM-07).</summary>
    public const string MarkValidated = "mark_validated";

    /// <summary>Task owner: deliver the verified result of the task this session works on (FR-A2A-09).</summary>
    public const string CompleteTask = "complete_task";

    /// <summary>Task owner: ask the requester a question; the task moves to <c>INPUT_REQUIRED</c> (FR-A2A-07).</summary>
    public const string RequestInput = "request_input";

    /// <summary>Requester: answer a delegated task's <c>INPUT_REQUIRED</c> question (FR-A2A-07).</summary>
    public const string AnswerTask = "answer_task";

    /// <summary>Tools that let a session reach other agents.</summary>
    public static readonly IReadOnlyList<string> AgentToAgent = [AskAgent, SendMessage, DelegateTask];

    /// <summary>Tools that read memory; absent from incognito sessions.</summary>
    public static readonly IReadOnlyList<string> MemoryReadTools = [MemoryList, MemoryRead, MemorySearch];

    /// <summary>Tools that mutate memory; absent from consult and incognito sessions.</summary>
    public static readonly IReadOnlyList<string> MemoryWrite = [Remember, Forget, Supersede, MarkValidated];

    /// <summary>
    /// Host protocol tools of a delegated-task session. They are added by the host to the task session only, never
    /// configured, and act solely on the session's own task (FR-A2A-07, FR-A2A-09).
    /// </summary>
    public static readonly IReadOnlyList<string> TaskProtocol = [CompleteTask, RequestInput];

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

    /// <summary>
    /// Incognito sessions read no memory and write none (FR-MEM-12), and cannot reach other agents either: anything sent
    /// to another agent could end up in that agent's memory.
    /// </summary>
    public static IReadOnlyList<string> ForIncognito(IEnumerable<string> tools, int depth) =>
        [.. ForDepth(tools, depth).Where(t =>
            !MemoryReadTools.Contains(t, StringComparer.Ordinal)
            && !MemoryWrite.Contains(t, StringComparer.Ordinal)
            && !AgentToAgent.Contains(t, StringComparer.Ordinal)
            && t != AnswerTask)];
}
