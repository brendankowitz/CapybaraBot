using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Capybara.Client;
using Capybara.Connections.Model;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Endpoints;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Runtime.Copilot.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Capybara.Gateway.Tests;

/// <summary>
/// A gateway with everything the client contract touches configured: a broker key, a webhook source, a VAPID key, one
/// broker connection and an agent template in the config copy.
/// </summary>
public class ApiFactory : GatewayFactory
{
    public const string BrokerKey = "test-broker-key-not-a-secret";
    public const string EventSource = "github";
    public const string EventSecret = "test-webhook-secret";
    public const string VapidKey = "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U";
    public const string Connection = "email";

    public ApiFactory()
    {
        // A template, so agents can be created from one (FR-AGT-07).
        var template = Path.Combine(ConfigRoot, "templates", "assistant");
        Directory.CreateDirectory(template);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ConfigRoot, "agents", "ea")))
        {
            File.Copy(file, Path.Combine(template, Path.GetFileName(file)));
        }

        Broker.Connections = [new ConnectionStatus(Connection, ConnectorKind.Gmail, CredentialProvider.Google, Authorized: true, Time.GetUtcNow().AddDays(30))];
    }

    public CapybaraApiClient Api(string token, RecordingHandler? recorder = null)
    {
        var client = recorder is null ? CreateClient() : CreateDefaultClient(recorder);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(TailnetAuthenticationMiddleware.TailscaleLoginHeader, Login);
        return new CapybaraApiClient(client);
    }

    protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
    [
        new("Capybara:Broker:ApiKey", BrokerKey),
        new($"Capybara:Events:Sources:{EventSource}:Secret", EventSecret),
    ];

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton(new WebPushPublicKey(VapidKey));
}

/// <summary>Uses the real SignalR broadcaster, so clients connected to the hub receive what the gateway publishes.</summary>
public sealed class RealtimeFactory : ApiFactory
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<ICapybaraEvents>();
        services.AddSingleton<ICapybaraEvents, CapybaraEventBroadcaster>();
    }
}

/// <summary>
/// Runtimes auto-start as fakes that answer every turn, so the inbox pumps run real turns end to end. The fakes keep
/// incognito sessions like the CLI does (created by a turn, last active at the turn, gone when ended).
/// </summary>
public sealed class ReplyingFactory : ApiFactory
{
    public ConcurrentQueue<AgentTurnRequest> Turns { get; } = new();

    /// <summary>Incognito session id → when it was last active, across every agent's fake CLI.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> IncognitoSessions { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Ended { get; } = new();

    /// <summary>When set, every turn waits for it.</summary>
    public TaskCompletionSource? TurnGate { get; set; }

    protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
        [.. base.Settings(), new("Capybara:Runtime:AutoStart", "true")];

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<AgentRuntimePool>();
        services.AddSingleton(sp => new AgentRuntimePool(sp.GetRequiredService<AgentRegistry>(), agent => new ReplyingRuntime(agent, this)));
    }

    private sealed class ReplyingRuntime(AgentDefinition initial, ReplyingFactory factory) : IAgentRuntime, ISupervisedRuntime
    {
        public AgentId Agent { get; } = initial.Id;

        public AgentDefinition Definition { get; private set; } = initial;

        public Task UpdateDefinitionAsync(AgentDefinition definition, CancellationToken cancellationToken)
        {
            Definition = definition;
            return Task.CompletedTask;
        }

        public async Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            factory.Turns.Enqueue(request);
            if (request.Target.Kind == SessionKind.Incognito)
            {
                factory.IncognitoSessions[request.Target.SessionIdFor(Agent)] = factory.Time.GetUtcNow();
            }

            if (factory.TurnGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return TurnResult.Completed($"reply to: {request.Prompt}", TurnEvidence.Empty);
        }

        public Task<TurnResult?> SteerAsync(string prompt, MessageOrigin origin, CancellationToken cancellationToken) => Task.FromResult<TurnResult?>(null);

        public Task SetModelAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken)
        {
            var id = target.SessionIdFor(Agent);
            factory.IncognitoSessions.TryRemove(id, out _);
            factory.Ended.Enqueue(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IncognitoSessionInfo>> ListIncognitoSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IncognitoSessionInfo>>([.. factory.IncognitoSessions
                .Where(s => SessionIds.TryParseIncognito(s.Key, out var owner, out _) && owner == Agent)
                .Select(s => new IncognitoSessionInfo(SessionIds.TryParseIncognito(s.Key, out _, out var conversation) ? conversation : s.Key, s.Value))]);

        public Task<ConsultReply> ConsultAsync(string sessionId, string question, Envelope envelope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken) => Task.FromResult(new SelfTestResult(true, [], []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
/// <summary>Records every request the client sends and what came back.</summary>
public sealed class RecordingHandler : DelegatingHandler
{
    private readonly ConcurrentQueue<Exchange> _exchanges = new();

    public IReadOnlyList<Exchange> Exchanges => [.. _exchanges];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
        _exchanges.Enqueue(new Exchange(request.Method.Method, request.RequestUri!.AbsolutePath, (int)response.StatusCode, response.Content?.Headers.ContentType?.MediaType, body));

        // The body was buffered by ReadAsStringAsync, so the caller can still read it.
        return response;
    }

    public sealed record Exchange(string Method, string Path, int Status, string? MediaType, string Body);
}
