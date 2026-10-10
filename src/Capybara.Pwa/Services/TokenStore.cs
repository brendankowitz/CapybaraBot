using Microsoft.JSInterop;

namespace Capybara.Pwa.Services;

/// <summary>Stores device tokens locally in the PWA; no tailnet identity header is synthesized (FR-CLI-01).</summary>
public sealed class TokenStore(IJSRuntime js)
{
    private const string TokenKey = "capybara.deviceToken";
    private const string NotificationKey = "capybara.notifications";

    public ValueTask<string?> GetTokenAsync() => js.InvokeAsync<string?>("capybaraStorage.get", TokenKey);

    public ValueTask SetTokenAsync(string token) => js.InvokeVoidAsync("capybaraStorage.set", TokenKey, token);

    public ValueTask<string?> GetNotificationPreferenceAsync() => js.InvokeAsync<string?>("capybaraStorage.get", NotificationKey);

    public ValueTask SetNotificationPreferenceAsync(string value) => js.InvokeVoidAsync("capybaraStorage.set", NotificationKey, value);
}
