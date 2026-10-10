namespace Capybara.Orchestration.Proposals;

/// <summary>
/// A <c>propose_change</c> request that is refused before it reaches the approval inbox: it touches grants,
/// connections or channels (FR-CON-07), another agent's files, or fails validation. The message is safe to return to
/// the proposing agent.
/// </summary>
public sealed class ProposalRejectedException : Exception
{
    public ProposalRejectedException()
    {
    }

    public ProposalRejectedException(string message)
        : base(message)
    {
    }

    public ProposalRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
