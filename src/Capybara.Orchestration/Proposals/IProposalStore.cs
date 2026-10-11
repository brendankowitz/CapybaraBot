using Capybara.Contracts.Proposals;

namespace Capybara.Orchestration.Proposals;

/// <summary>Durable record of change proposals, so the approval inbox survives restarts (FR-AGT-06).</summary>
public interface IProposalStore
{
    /// <summary>Creates or replaces the proposal with the same id.</summary>
    ValueTask SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken = default);

    ValueTask<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChangeProposal> ListAsync(CancellationToken cancellationToken = default);
}
