using Capybara.Contracts.Approvals;

namespace Capybara.Policy.Approvals;

/// <summary>Durable backing for <see cref="ApprovalService"/> so pending approvals survive a restart (FR-SAF-04, NFR-02).</summary>
public interface IApprovalStore
{
    /// <summary>The latest persisted state of every approval that is still live. Called once at construction.</summary>
    IReadOnlyList<ApprovalRequest> Load();

    /// <summary>Persists the new state of one approval. Later saves for the same id supersede earlier ones.</summary>
    void Save(ApprovalRequest request);
}
