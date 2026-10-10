using System.Net.Http.Json;

namespace Capybara.Connections.OAuth;

/// <summary>
/// Posts consent requests to capybarad, which turns them into a "needs input" notification on the user's device
/// (FR-CON-08). Contract: <c>POST /api/connections/auth-requests</c> with an <see cref="InteractiveAuthRequest"/> JSON
/// body and <c>Authorization: Bearer {broker-api-key}</c>; capybarad must reject any other caller, because a forged
/// request would put an attacker's link on the user's phone. The <see cref="HttpClient"/> is configured with
/// capybarad's base address and that bearer.
/// </summary>
public sealed class CapybaradInteractiveAuthRouter(HttpClient http) : IInteractiveAuthRouter
{
    public const string Route = "/api/connections/auth-requests";

    public async ValueTask RouteAsync(InteractiveAuthRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Route, request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
