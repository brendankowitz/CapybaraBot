namespace Fleet.Policy.Review;

/// <summary>
/// The reviewer agent (FR-SAF-05): a separate, tool-less, cheap-model session with a fixed rubric. It is only consulted
/// for calls the deterministic rules leave unresolved, so it can never override an ask-first or deny rule.
/// </summary>
public interface IToolCallReviewer
{
    Task<ReviewResult> ReviewAsync(Gate.ToolCallContext toolCall, CancellationToken cancellationToken);
}
