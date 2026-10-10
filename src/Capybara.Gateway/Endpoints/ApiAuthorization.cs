using Capybara.Gateway.Security;
using Capybara.Policy.Safety;

namespace Capybara.Gateway.Endpoints;

/// <summary>Endpoint-level authorization on top of <see cref="TailnetAuthenticationMiddleware"/> (FR-HST-04, FR-SAF-06).</summary>
public static class ApiAuthorization
{
    /// <summary>
    /// Every endpoint in the group needs a device the middleware authenticated with its token. Defence in depth: the
    /// middleware already refuses tokenless requests under <c>/api</c>; this also holds if a path ever slipped past it.
    /// </summary>
    public static RouteGroupBuilder RequireDevice(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(static (context, next) =>
            context.HttpContext.TryGetDevice(out _) ? next(context) : ValueTask.FromResult<object?>(TypedResults.Unauthorized()));

    /// <summary>Desktop-surface devices only; any other device gets 403 before the handler runs.</summary>
    public static TBuilder RequireDesktop<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(static (context, next) =>
            context.HttpContext.TryGetDevice(out var device) && device.Surface == ControlSurface.Desktop
                ? next(context)
                : ValueTask.FromResult<object?>(Forbidden("This action is allowed from the desktop only.")));

    /// <summary>403 with a short reason the clients show.</summary>
    public static IResult Forbidden(string reason) => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: reason);
}
