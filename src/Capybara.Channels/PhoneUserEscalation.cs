using System.Collections.Concurrent;
using Capybara.Contracts.Agents;

namespace Capybara.Channels;

/// <summary>Phone-backed ask_user escalation that returns null when the human does not answer before timeout (FR-A2A-07).</summary>
/// <remarks>
/// Pending questions are kept so the PWA deep link (<c>/questions/{id}</c>) can show one while it is open; once answered
/// or timed out it is gone. <see cref="Asked"/> is raised synchronously before the phone push is sent; subscribers must
/// return quickly.
/// </remarks>
public sealed class PhoneUserEscalation(NotificationRouter router, TimeProvider timeProvider) : IUserEscalation
{
    private readonly ConcurrentDictionary<string, (PendingQuestion Question, TaskCompletionSource<string> Answer)> _pending = new(StringComparer.Ordinal);

    /// <summary>A question is now waiting for the user.</summary>
    public event EventHandler<PendingQuestion>? Asked;

    public async ValueTask<string?> AskAsync(string questionId, AgentId agent, string title, string body, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        var question = new PendingQuestion(questionId, agent, title, body, timeProvider.GetUtcNow() + timeout);
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(questionId, (question, source)))
        {
            throw new InvalidOperationException($"Question '{questionId}' is already pending.");
        }

        try
        {
            Asked?.Invoke(this, question);
            await router.RouteAsync(new Notification(
                NotificationKind.NeedsInput,
                agent,
                title,
                body,
                NotificationPriority.High,
                $"/questions/{Uri.EscapeDataString(questionId)}",
                IsSensitive: true), cancellationToken);

            var completed = await Task.WhenAny(source.Task, Task.Delay(timeout, timeProvider, cancellationToken));
            return completed == source.Task ? await source.Task : null;
        }
        finally
        {
            _pending.TryRemove(questionId, out _);
        }
    }

    /// <summary>The question while it is still waiting for an answer, else <see langword="null"/>.</summary>
    public PendingQuestion? Find(string questionId) =>
        _pending.TryGetValue(questionId, out var pending) && !pending.Answer.Task.IsCompleted ? pending.Question : null;

    public bool SubmitAnswer(string questionId, string answer) =>
        _pending.TryGetValue(questionId, out var pending) && pending.Answer.TrySetResult(answer);
}
