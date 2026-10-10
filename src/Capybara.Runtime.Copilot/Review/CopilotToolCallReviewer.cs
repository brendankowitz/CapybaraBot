using Capybara.Policy.Gate;
using Capybara.Policy.Review;
using Capybara.Runtime.Copilot.Configuration;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Review;

/// <summary>
/// The reviewer agent (FR-SAF-05): the fixed <see cref="ReviewerRubric"/> prompt, one model call, and
/// <see cref="ReviewVerdictParser"/>. It fails toward a human: arguments too large to show in full, a model or
/// transport failure, a timeout or unparseable output all come back as <see cref="ReviewVerdict.Escalate"/>. The gate
/// consults it only for calls the deterministic rules left unresolved, so it can never override an ask-first or deny
/// rule.
/// </summary>
public sealed partial class CopilotToolCallReviewer(
    IReviewerModel model,
    CopilotRuntimeOptions options,
    TimeProvider time,
    ILogger<CopilotToolCallReviewer> logger) : IToolCallReviewer
{
    public async Task<ReviewResult> ReviewAsync(ToolCallContext toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // The gate already escalates these; checked again so the model can never vouch for a call it cannot see whole.
        if (ReviewerRubric.ExceedsLimit(toolCall))
        {
            return ReviewerRubric.TruncatedResult;
        }

        using var timeout = new CancellationTokenSource(options.ReviewerTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return ReviewVerdictParser.Parse(await model.CompleteAsync(toolCall.Agent.Id, ReviewerRubric.Build(toolCall), linked.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(logger, toolCall.Tool, toolCall.Agent.Id.Value);
            return new ReviewResult(ReviewVerdict.Escalate, "the reviewer did not answer in time");
        }
#pragma warning disable CA1031 // Any reviewer failure means a human decides.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, toolCall.Tool, toolCall.Agent.Id.Value);
            return new ReviewResult(ReviewVerdict.Escalate, "the reviewer is unavailable");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of {Tool} for {Agent} timed out; escalating")]
    private static partial void LogTimedOut(ILogger logger, string tool, string agent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review of {Tool} for {Agent} failed; escalating")]
    private static partial void LogFailed(ILogger logger, Exception exception, string tool, string agent);
}
