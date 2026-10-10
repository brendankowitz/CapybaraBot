using System.Collections.Concurrent;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>An <see cref="IAgentRuntime"/> without a CLI: records every call and runs scripted turns.</summary>
internal sealed class FakeAgentRuntime(AgentId agent) : IAgentRuntime
{
    private readonly Lock _gate = new();
    private (bool Steerable, TaskCompletionSource<TurnResult> Done)? _inFlight;

    public AgentId Agent { get; } = agent;

    public AgentDefinition Definition { get; private set; } = Testing.TestAgents.Create(agent.Value);

    public Task<DefinitionChange> UpdateDefinitionAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        var change = DefinitionChanges.Classify(Definition, definition);
        Definition = definition;
        return Task.FromResult(change);
    }

    /// <summary>The turn body; completes with "done" by default.</summary>
    public Func<AgentTurnRequest, CancellationToken, Task<TurnResult>> Turn { get; set; } =
        (_, _) => Task.FromResult(TurnResult.Completed("done", TurnEvidence.Empty));

    public ConcurrentQueue<AgentTurnRequest> Requests { get; } = new();

    public ConcurrentQueue<string> Steered { get; } = new();

    public ConcurrentQueue<TurnTarget> Ended { get; } = new();

    public List<string> Models { get; } = [];

    public async Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (request.Target.Kind == SessionKind.Main)
        {
            lock (_gate)
            {
                _inFlight = (!request.DryRun, done);
            }
        }

        TurnResult? result = null;
        try
        {
            result = await Turn(request, cancellationToken);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight?.Done == done)
                {
                    _inFlight = null;
                }
            }

            done.TrySetResult(result ?? TurnResult.Failed("threw"));
        }
    }

    public async Task<TurnResult?> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken)
    {
        (bool Steerable, TaskCompletionSource<TurnResult> Done)? turn;
        lock (_gate)
        {
            turn = _inFlight;
        }

        if (turn is not { Steerable: true } current)
        {
            return null;
        }

        Steered.Enqueue(prompt);
        return await current.Done.Task.WaitAsync(cancellationToken);
    }

    public Task SetModelAsync(string model, CancellationToken cancellationToken)
    {
        Models.Add(model);
        return Task.CompletedTask;
    }

    public Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken)
    {
        Ended.Enqueue(target);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IncognitoSessionInfo>> ListIncognitoSessionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IncognitoSessionInfo>>([]);

    public Task<ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken) =>
        Task.FromResult(new ConsultReply("42", Taint.Clean, []));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Eventually
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="timeout"/>.</summary>
    public static async Task TrueAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public static Task TrueAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        TrueAsync(() => Task.FromResult(condition()), what, timeout);
}
