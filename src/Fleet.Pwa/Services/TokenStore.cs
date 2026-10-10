using Microsoft.JSInterop;

namespace Fleet.Pwa.Services;

/// <summary>Stores device tokens locally in the PWA; no tailnet identity header is synthesized (FR-CLI-01).</summary>
public sealed class TokenStore(IJSRuntime js)
{
    private const string TokenKey = "fleet.deviceToken";
    private const string NotificationKey = "fleet.notifications";

    public ValueTask<string?> GetTokenAsync() => js.InvokeAsync<string?>("fleetStorage.get", TokenKey);

    public ValueTask SetTokenAsync(string token) => js.InvokeVoidAsync("fleetStorage.set", TokenKey, token);

    public ValueTask<string?> GetNotificationPreferenceAsync() => js.InvokeAsync<string?>("fleetStorage.get", NotificationKey);

    public ValueTask SetNotificationPreferenceAsync(string value) => js.InvokeVoidAsync("fleetStorage.set", NotificationKey, value);
}
