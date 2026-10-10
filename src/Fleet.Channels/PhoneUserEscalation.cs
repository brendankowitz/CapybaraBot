using System.Collections.Concurrent;
using Fleet.Contracts.Agents;

namespace Fleet.Channels;

/// <summary>Phone-backed ask_user escalation that returns null when the human does not answer before timeout (FR-A2A-07).</summary>
public sealed class PhoneUserEscalation(NotificationRouter router, TimeProvider timeProvider) : IUserEscalation
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);

    public async ValueTask<string?> AskAsync(string questionId, AgentId agent, string title, string body, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(questionId, source))
        {
            throw new InvalidOperationException($"Question '{questionId}' is already pending.");
        }

        try
        {
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

    public bool SubmitAnswer(string questionId, string answer) =>
        _pending.TryGetValue(questionId, out var source) && source.TrySetResult(answer);
}
