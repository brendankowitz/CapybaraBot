namespace Capybara.Gateway.Endpoints;

/// <summary>
/// The REST surface for the PWA and TUI (FR-CLI-02, FR-CLI-05), as specified by <c>Capybara.Client/README-contract.md</c>.
/// Everything under <c>/api</c> needs an authenticated device; the one exception, the broker's consent route, is
/// mapped separately with its own credential.
/// </summary>
public static class CapybaraApi
{
    public static IEndpointRouteBuilder MapCapybaraApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api").RequireDevice();
        api.MapAgentEndpoints();
        api.MapLifecycleEndpoints();
        api.MapSafetyEndpoints();
        api.MapDecisionEndpoints();
        api.MapMemoryEndpoints();
        api.MapRoutineEndpoints();
        api.MapObservabilityEndpoints();
        api.MapConnectionEndpoints();
        api.MapPushEndpoints();
        api.MapEventIngressEndpoints();
        app.MapBrokerConsentEndpoint();
        return app;
    }
}
