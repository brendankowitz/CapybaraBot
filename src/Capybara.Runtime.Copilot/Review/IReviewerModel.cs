using Capybara.Contracts.Agents;

namespace Capybara.Runtime.Copilot.Review;

/// <summary>
/// The reviewer's model call (FR-SAF-05): one fixed-rubric prompt in, the model's raw text out. The boundary to the
/// Copilot CLI, so the review logic can be exercised without one.
/// </summary>
public interface IReviewerModel
{
    /// <param name="reviewed">The agent whose tool call is being reviewed; the review's spend is charged to it.</param>
    /// <param name="prompt">The complete reviewer prompt.</param>
    /// <param name="cancellationToken">Cancels the review.</param>
    Task<string?> CompleteAsync(AgentId reviewed, string prompt, CancellationToken cancellationToken);
}
