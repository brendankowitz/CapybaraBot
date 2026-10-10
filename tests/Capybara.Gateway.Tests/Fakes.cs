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

    public IReadOnlyList<string> Requests => [.. _requests];

    public bool Received(string request) => _requests.Contains(request, StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue($"{request.Method} {path}");
        var response = (request.Method.Method, path) switch
        {
            ("POST", "/broker/freeze") when FreezeStatus == HttpStatusCode.OK => Json(new FreezeOutcome(null, 0)),
            ("POST", "/broker/freeze") => new HttpResponseMessage(FreezeStatus),
            ("POST", "/broker/unfreeze") => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("POST", _) when path.StartsWith("/broker/agents/", StringComparison.Ordinal) && path.EndsWith("/revoke", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NoContent),
            ("GET", "/broker/credentials/health") => Json(Array.Empty<CredentialHealth>()),
            ("GET", _) when path.StartsWith("/broker/tools/", StringComparison.Ordinal) => Json(Array.Empty<BrokerToolDescriptor>()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
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

/// <summary>A push channel that accepts everything and records it.</summary>
public sealed class RecordingChannel : INotificationChannel
{
    private readonly ConcurrentQueue<Notification> _sent = new();

    public string Name => "recording";

    public bool SupportsSensitiveContent => true;

    public IReadOnlyList<Notification> Sent => [.. _sent];

    public ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(notification);
        return ValueTask.CompletedTask;
    }
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
