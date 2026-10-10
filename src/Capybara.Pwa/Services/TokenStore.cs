using Microsoft.JSInterop;

namespace Capybara.Pwa.Services;

/// <summary>Stores the device token locally in the PWA; no tailnet identity header is synthesized (FR-CLI-01).</summary>
public sealed class TokenStore(IJSRuntime js)
{
    private const string TokenKey = "capybara.deviceToken";

    public ValueTask<string?> GetTokenAsync() => js.InvokeAsync<string?>("capybaraStorage.get", TokenKey);

    public ValueTask SetTokenAsync(string token) => js.InvokeVoidAsync("capybaraStorage.set", TokenKey, token);
}
