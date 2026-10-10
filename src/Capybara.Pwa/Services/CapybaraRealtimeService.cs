using Capybara.Client;
using Microsoft.AspNetCore.Components;

namespace Capybara.Pwa.Services;

/// <summary>
/// Live updates over SignalR (FR-CLI-02). Delivery is best effort: the REST API stays authoritative, so a failed
/// connection only means pages do not refresh on their own. Pages must unsubscribe from the events they use.
/// </summary>
public class CapybaraRealtimeService(NavigationManager navigation, TokenStore tokens) : IAsyncDisposable
{
    private CapybaraRealtimeClient? _client;
    private Task<bool>? _starting;

    public event Action<string, string, int>? AgentStatusChanged;

    public event Action<ThreadEntryDto>? ThreadEntry;

    public event Action<ApprovalDto>? ApprovalRequested;

    public event Action<string>? ApprovalResolved;

    public event Action<QuestionDto>? QuestionRequested;

    public event Action<AlertDto>? Alert;

    public event Action<bool>? KillSwitchChanged;

    /// <summary>Connects once; concurrent callers share the attempt. Returns false (and allows a retry) when it fails.</summary>
    public virtual Task<bool> TryStartAsync() => _starting ??= StartCoreAsync();

    private async Task<bool> StartCoreAsync()
    {
        try
        {
            _client ??= Create();
            await _client.StartAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _starting = null;
            return false;
        }
    }

    private CapybaraRealtimeClient Create()
    {
        var client = new CapybaraRealtimeClient(new Uri(navigation.BaseUri), () => tokens.GetTokenAsync().AsTask());
        client.OnAgentStatusChanged((id, state, unread) => AgentStatusChanged?.Invoke(id, state, unread));
        client.OnThreadEntry(entry => ThreadEntry?.Invoke(entry));
        client.OnApprovalRequested(approval => ApprovalRequested?.Invoke(approval));
        client.OnApprovalResolved(id => ApprovalResolved?.Invoke(id));
        client.OnQuestionRequested(question => QuestionRequested?.Invoke(question));
        client.OnAlert(alert => Alert?.Invoke(alert));
        client.OnKillSwitchChanged(engaged => KillSwitchChanged?.Invoke(engaged));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
