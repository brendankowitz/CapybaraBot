namespace Fleet.Contracts.Agents;

/// <summary>Per-agent peer allowlist, separate for consult, message and task. Default deny (FR-A2A-10).</summary>
public sealed record PeerAllowlist
{
    public IReadOnlyList<string> Consult { get; init; } = [];

    public IReadOnlyList<string> Message { get; init; } = [];

    public IReadOnlyList<string> Task { get; init; } = [];

    public bool Allows(A2AOperation operation, AgentId target) => operation switch
    {
        A2AOperation.Consult => Consult.Contains(target.Value, StringComparer.Ordinal),
        A2AOperation.Message => Message.Contains(target.Value, StringComparer.Ordinal),
        A2AOperation.Task => Task.Contains(target.Value, StringComparer.Ordinal),
        _ => false,
    };

    public bool AllowsAny(AgentId target) =>
        Allows(A2AOperation.Consult, target) || Allows(A2AOperation.Message, target) || Allows(A2AOperation.Task, target);
}
