using Capybara.Client;
using Microsoft.AspNetCore.Components;

namespace Capybara.Pwa.Services;

public sealed class CapybaraRealtimeService(NavigationManager navigation, TokenStore tokens) : IAsyncDisposable
{
    private CapybaraRealtimeClient? _client;

    public event Action<string, string, int>? AgentStatusChanged;

    public event Action<bool>? KillSwitchChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _client ??= Create();
        if (_client.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected)
        {
            await _client.StartAsync(cancellationToken);
        }
    }

    private CapybaraRealtimeClient Create()
    {
        var client = new CapybaraRealtimeClient(new Uri(navigation.BaseUri), () => tokens.GetTokenAsync().AsTask());
        client.OnAgentStatusChanged((id, state, unread) => AgentStatusChanged?.Invoke(id, state, unread));
        client.OnKillSwitchChanged(engaged => KillSwitchChanged?.Invoke(engaged));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
