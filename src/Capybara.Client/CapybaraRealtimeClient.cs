using Microsoft.AspNetCore.SignalR.Client;

namespace Capybara.Client;

/// <summary>
/// SignalR client for live fleet updates; browsers use the access_token query flow (FR-CLI-02). The hub's JSON protocol
/// must write enums as strings, so payloads match the REST DTOs.
/// </summary>
public sealed class CapybaraRealtimeClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public CapybaraRealtimeClient(Uri baseAddress, Func<Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/hubs/capybara"), options => options.AccessTokenProvider = accessTokenProvider)
            .WithAutomaticReconnect()
            .Build();
    }

    public HubConnectionState State => _connection.State;

    public Task StartAsync(CancellationToken cancellationToken = default) => _connection.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _connection.StopAsync(cancellationToken);

    public IDisposable OnAgentStatusChanged(Action<string, string, int> handler) => _connection.On("AgentStatusChanged", handler);

    public IDisposable OnThreadEntry(Action<ThreadEntryDto> handler) => _connection.On("ThreadEntry", handler);

    public IDisposable OnApprovalRequested(Action<ApprovalDto> handler) => _connection.On("ApprovalRequested", handler);

    public IDisposable OnApprovalResolved(Action<string> handler) => _connection.On("ApprovalResolved", handler);

    public IDisposable OnQuestionRequested(Action<QuestionDto> handler) => _connection.On("QuestionRequested", handler);

    public IDisposable OnAlert(Action<AlertDto> handler) => _connection.On("Alert", handler);

    public IDisposable OnKillSwitchChanged(Action<bool> handler) => _connection.On("KillSwitchChanged", handler);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
