using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Gateway.Security;
using Capybara.Gateway.Settings;
using Capybara.Orchestration.Registry;
using Microsoft.AspNetCore.Mvc;

namespace Capybara.Gateway.Endpoints;

/// <summary>Web Push subscriptions, PWA presence and notification settings (FR-CLI-06, FR-CLI-07).</summary>
public static class PushEndpoints
{
    /// <summary>
    /// The push services browsers subscribe through. The gateway POSTs to whatever endpoint is stored, so anything else
    /// (a tailnet or loopback address such as the broker) is refused rather than turned into a request forgery.
    /// </summary>
    public static readonly IReadOnlyList<string> PushHosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder api)
    {
        var push = api.MapGroup("/push").WithTags("Push");

        push.MapGet("/vapid-key", ([FromServices] WebPushPublicKey? key) =>
            key is null ? Results.NotFound("web push is not configured") : Results.Ok(new VapidKeyDto(key.Value)));

        // The subscription belongs to the authenticated device: one per device, replaced on re-subscribe.
        push.MapPost("/subscriptions", (PushSubscriptionDto request, HttpContext context, WebPushSubscriptionStore store) =>
        {
            if (!IsPushEndpoint(request.Endpoint, out var endpoint))
            {
                return Results.BadRequest("endpoint must be an https URL of a browser push service");
            }

            if (request.Keys is null || !IsBase64Url(request.Keys.P256dh, 65) || !IsBase64Url(request.Keys.Auth, 16))
            {
                return Results.BadRequest("keys.p256dh must be a 65-byte P-256 point and keys.auth 16 bytes, base64url");
            }

            store.Upsert(new WebPushSubscription(context.GetDevice().Device, endpoint, request.Keys.P256dh, request.Keys.Auth));
            return Results.NoContent();
        });

        push.MapDelete("/subscriptions", (string? endpoint, HttpContext context, WebPushSubscriptionStore store) =>
        {
            var device = context.GetDevice().Device;
            if (store.Subscriptions.Any(s => s.DeviceId == device && string.Equals(s.Endpoint.ToString(), endpoint, StringComparison.Ordinal)))
            {
                store.Remove(device);
            }

            return Results.NoContent();
        });

        // Focus is per device and expires on its own (ChannelOptions.PresenceFocusTtl), so a crashed tab cannot keep
        // push suppressed; approvals are never suppressed (FR-CLI-07).
        api.MapPost("/presence", (PresenceDto request, HttpContext context, IPresenceTracker presence) =>
        {
            presence.SetFocused(context.GetDevice().Device, request.Focused);
            return Results.NoContent();
        }).WithTags("Push");

        var settings = api.MapGroup("/settings/notifications").WithTags("Push");

        settings.MapGet("/", (NotificationSettings store) => TypedResults.Ok(store.Get()));

        settings.MapPut("/", (NotificationSettingsDto request, NotificationSettings store, AgentRegistry registry) =>
        {
            if (request.QuietHours is null
                || !NotificationSettings.TryParseTime(request.QuietHours.Start, out var start)
                || !NotificationSettings.TryParseTime(request.QuietHours.End, out var end))
            {
                return Results.BadRequest("quietHours needs start and end as HH:mm");
            }

            var toggles = new Dictionary<AgentId, bool>();
            foreach (var toggle in request.Agents ?? [])
            {
                if (!AgentId.TryParse(toggle.Agent, out var agent) || registry.Find(agent) is null)
                {
                    return Results.BadRequest($"unknown agent '{toggle.Agent}'");
                }

                toggles[agent] = toggle.Enabled;
            }

            store.Save(toggles, new QuietHoursOptions { Enabled = request.QuietHours.Enabled, StartsAt = start, EndsAt = end });
            return Results.NoContent();
        });

        return api;
    }

    private static bool IsPushEndpoint(string? value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            return false;
        }

        var host = uri.IdnHost;
        if (!PushHosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        endpoint = uri;
        return true;
    }

    private static bool IsBase64Url(string? value, int bytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2 * bytes || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '='))
        {
            return false;
        }

        var padded = value.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        Span<byte> decoded = stackalloc byte[bytes + 3];
        return Convert.TryFromBase64String(padded, decoded, out var written) && written == bytes;
    }
}

/// <summary>The VAPID public key clients pass to <c>pushManager.subscribe</c>; registered only when web push is configured.</summary>
public sealed record WebPushPublicKey(string Value);
