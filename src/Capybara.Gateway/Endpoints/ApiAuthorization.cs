using Capybara.Gateway.Security;
using Capybara.Policy.Safety;

namespace Capybara.Gateway.Endpoints;

/// <summary>Endpoint-level authorization on top of <see cref="TailnetAuthenticationMiddleware"/> (FR-CLI-01, FR-SAF-06).</summary>
public static class ApiAuthorization
{
    /// <summary>
    /// Every endpoint in the group needs a person's device that the middleware authenticated with its token; an
    /// <see cref="ControlSurface.EventRelay"/> token is refused except on endpoints marked <see cref="ForEventSenders"/>.
    /// Defence in depth: the middleware already enforces both.
    /// </summary>
    public static RouteGroupBuilder RequireDevice(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(static (context, next) =>
        {
            if (!context.HttpContext.TryGetDevice(out var device))
            {
                return ValueTask.FromResult<object?>(TypedResults.Unauthorized());
            }

            return device.Surface == ControlSurface.EventRelay && !EventRelayEndpoint.Allows(context.HttpContext)
                ? ValueTask.FromResult<object?>(Forbidden("An event relay token may only post events."))
                : next(context);
        });

    /// <summary>Desktop-surface devices only; any other device gets 403 before the handler runs.</summary>
    public static TBuilder RequireDesktop<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(static (context, next) =>
            context.HttpContext.TryGetDevice(out var device) && device.Surface == ControlSurface.Desktop
                ? next(context)
                : ValueTask.FromResult<object?>(Forbidden("This action is allowed from the desktop only.")));

    /// <summary>
    /// The webhook ingress: open to <see cref="ControlSurface.EventRelay"/> tokens (the only thing they open) and, for
    /// convenience, desktop devices; refused to phones and the TUI.
    /// </summary>
    public static TBuilder ForEventSenders<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .WithMetadata(EventRelayEndpoint.Instance)
            .AddEndpointFilter(static (context, next) =>
                context.HttpContext.TryGetDevice(out var device) && device.Surface is ControlSurface.EventRelay or ControlSurface.Desktop
                    ? next(context)
                    : ValueTask.FromResult<object?>(Forbidden("Events are posted by an event relay or the desktop.")));

    /// <summary>403 with a short reason the clients show.</summary>
    public static IResult Forbidden(string reason) => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: reason);
}

/// <summary>Endpoint metadata: an <see cref="ControlSurface.EventRelay"/> token may call this endpoint.</summary>
public sealed class EventRelayEndpoint
{
    public static EventRelayEndpoint Instance { get; } = new();

    private EventRelayEndpoint()
    {
    }

    public static bool Allows(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<EventRelayEndpoint>() is not null;
}
