using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Capybara.Channels;
using Capybara.Connections.Broker;
using Capybara.Connections.Health;
using Capybara.Connections.Model;
using Capybara.Gateway.Realtime;

namespace Capybara.Gateway.Tests;

/// <summary>Answers the broker routes capybarad calls and records every request (method and path).</summary>
public sealed class FakeBrokerHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _requests = new();

    /// <summary>Status for <c>POST /broker/freeze</c>; 500 is the broker's "frozen, but not durably".</summary>
    public HttpStatusCode FreezeStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>When set, <c>POST /broker/freeze</c> never answers (until the caller gives up).</summary>
    public bool HangFreeze { get; set; }

    public IReadOnlyList<string> Requests => [.. _requests];

    public bool Received(string request) => _requests.Contains(request, StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue($"{request.Method} {path}");
        if (HangFreeze && path == "/broker/freeze")
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return (request.Method.Method, path) switch
        {
            ("POST", "/broker/freeze") when FreezeStatus == HttpStatusCode.OK => Json(new FreezeOutcome(null, 0)),
            ("POST", "/broker/freeze") => new HttpResponseMessage(FreezeStatus),
            ("POST", "/broker/unfreeze") => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("POST", _) when path.StartsWith("/broker/agents/", StringComparison.Ordinal) && path.EndsWith("/revoke", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("GET", "/broker/credentials/health") => Json(Array.Empty<CredentialHealth>()),
            ("GET", _) when path.StartsWith("/broker/tools/", StringComparison.Ordinal) => Json(Array.Empty<BrokerToolDescriptor>()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: BrokerJson.Options) };
}

/// <summary>Records every realtime broadcast instead of sending it over SignalR.</summary>
public sealed class RecordingEvents : ICapybaraEvents
{
    private readonly ConcurrentQueue<(string Name, object? Payload)> _published = new();

    public IReadOnlyList<(string Name, object? Payload)> Published => [.. _published];

    public List<AgentActivitySnapshot> Records { get; } = [];

    public Task PublishAgentStatusChangedAsync(string agentId, AgentActivityState state, int unread, CancellationToken cancellationToken = default)
    {
        lock (Records)
        {
            Records.Add(new AgentActivitySnapshot(agentId, state, unread));
        }

        return Record("AgentStatusChanged", new AgentActivitySnapshot(agentId, state, unread));
    }

    public Task PublishThreadEntryAsync(object entry, CancellationToken cancellationToken = default) => Record("ThreadEntry", entry);

    public Task PublishApprovalRequestedAsync(object approval, CancellationToken cancellationToken = default) => Record("ApprovalRequested", approval);

    public Task PublishApprovalResolvedAsync(string id, CancellationToken cancellationToken = default) => Record("ApprovalResolved", id);

    public Task PublishAlertAsync(object alert, CancellationToken cancellationToken = default) => Record("Alert", alert);

    public Task PublishKillSwitchChangedAsync(bool engaged, CancellationToken cancellationToken = default) => Record("KillSwitchChanged", engaged);

    public Task PublishProposalAsync(object proposal, CancellationToken cancellationToken = default) => Record("ProposalCreated", proposal);

    private Task Record(string name, object? payload)
    {
        _published.Enqueue((name, payload));
        return Task.CompletedTask;
    }
}

/// <summary>A push channel that accepts everything and records it; <see cref="Hold"/> makes sends hang until released.</summary>
public sealed class RecordingChannel : INotificationChannel
{
    private readonly ConcurrentQueue<Notification> _sent = new();

    public string Name => "recording";

    public bool SupportsSensitiveContent => true;

    public IReadOnlyList<Notification> Sent => [.. _sent];

    /// <summary>When set, every send waits for this before it is recorded.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        _sent.Enqueue(notification);
    }
}

/// <summary>A runtime without a CLI that records starts, stops and restarts.</summary>
public sealed class FakeSupervisedRuntime(Contracts.Agents.AgentId agent) : Runtime.Copilot.Hosting.IAgentRuntime, Runtime.Copilot.Supervision.ISupervisedRuntime
{
    private int _starts;
    private int _restarts;
    private int _stops;
    private volatile bool _running;

    public Contracts.Agents.AgentId Agent { get; } = agent;

    public int Starts => _starts;

    public int Restarts => _restarts;

    public int Stops => _stops;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stops);
        _running = false;
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _restarts);
        _running = true;
        RestartEntered.TrySetResult();
        return RestartGate is { } gate ? gate.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>When set, restarts count and then wait for it, so a test can act in the middle of a restart pass.</summary>
    public TaskCompletionSource? RestartGate { get; set; }

    public TaskCompletionSource RestartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(_running);

    public Task<Runtime.Copilot.Hosting.SelfTestResult> SelfTestAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new Runtime.Copilot.Hosting.SelfTestResult(true, [], []));

    public Task<Runtime.Copilot.Hosting.TurnResult> RunTurnAsync(Runtime.Copilot.Hosting.AgentTurnRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Runtime.Copilot.Hosting.TurnResult.Blocked("fake"));

    public Task<Runtime.Copilot.Hosting.TurnResult?> SteerAsync(string prompt, Contracts.Messaging.MessageOrigin origin, CancellationToken cancellationToken) =>
        Task.FromResult<Runtime.Copilot.Hosting.TurnResult?>(null);

    public Task SetModelAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EndSessionAsync(Runtime.Copilot.Hosting.TurnTarget target, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Orchestration.Coordination.ConsultReply> ConsultAsync(string sessionId, string question, Contracts.Messaging.Envelope envelope, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fake runtime does not answer consults.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Eventually
{
    /// <summary>Polls on wall-clock time: bridges hand work to the thread pool, so effects land shortly after the event.</summary>
    public static async Task TrueAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting until {because}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public static async Task TrueAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting until {because}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
