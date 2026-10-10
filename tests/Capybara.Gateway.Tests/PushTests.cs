using System.Net;
using System.Net.Http.Json;
using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Capybara.Gateway.Tests;

/// <summary>WP11 acceptance 8: push subscriptions, presence and notification settings (FR-CLI-06, FR-CLI-07).</summary>
public sealed class PushTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static PushSubscriptionDto ValidSubscription(string endpoint = "https://fcm.googleapis.com/fcm/send/abc123") =>
        new(endpoint, new PushKeysDto(Base64Url([0x04, .. new byte[64]]), Base64Url(new byte[16])));

    [Fact]
    public async Task A_subscription_belongs_to_the_authenticated_device_and_is_removed_by_endpoint()
    {
        var api = factory.Api(factory.PhoneToken);
        var store = factory.Services.GetRequiredService<WebPushSubscriptionStore>();

        await api.AddPushSubscriptionAsync(ValidSubscription("https://web.push.apple.com/abc"), Ct);
        Assert.Contains(store.Subscriptions, s => s.DeviceId == "phone" && s.Endpoint.Host == "web.push.apple.com");

        await api.RemovePushSubscriptionAsync("https://web.push.apple.com/other", Ct);
        Assert.Contains(store.Subscriptions, s => s.DeviceId == "phone");

        await api.RemovePushSubscriptionAsync("https://web.push.apple.com/abc", Ct);
        Assert.DoesNotContain(store.Subscriptions, s => s.DeviceId == "phone");
    }

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://127.0.0.1:8788/broker/freeze")]
    [InlineData("https://evil.example/push")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    [InlineData("https://fcm.googleapis.com:8443/x")]
    [InlineData("https://user@fcm.googleapis.com/x")]
    [InlineData("not a url")]
    public async Task Push_endpoints_outside_the_browser_push_services_are_refused(string endpoint)
    {
        var client = factory.CreateDeviceClient(factory.PhoneToken);

        var response = await client.PostAsJsonAsync(new Uri("/api/push/subscriptions", UriKind.Relative), ValidSubscription(endpoint), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_subscription_keys_are_refused()
    {
        var client = factory.CreateDeviceClient(factory.PhoneToken);
        var subscription = ValidSubscription() with { Keys = new PushKeysDto("short", Base64Url(new byte[16])) };

        var response = await client.PostAsJsonAsync(new Uri("/api/push/subscriptions", UriKind.Relative), subscription, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Presence_is_tracked_per_device_and_cleared_on_blur()
    {
        var api = factory.Api(factory.DesktopToken);
        var presence = factory.Services.GetRequiredService<IPresenceTracker>();

        await api.SetPresenceAsync(focused: true, Ct);
        Assert.True(presence.IsAnyFocused());

        await api.SetPresenceAsync(focused: false, Ct);
        Assert.False(presence.IsAnyFocused());
    }

    [Fact]
    public async Task Notification_settings_are_saved_applied_to_the_router_and_read_back()
    {
        var api = factory.Api(factory.PhoneToken);

        await api.SaveNotificationSettingsAsync(new NotificationSettingsDto([new AgentNotificationToggleDto("research", false)], new QuietHoursDto(true, "21:30", "06:45")), Ct);

        var settings = await api.GetNotificationSettingsAsync(Ct);
        Assert.Contains(settings.Agents, a => a is { Agent: "research", Enabled: false });
        Assert.Contains(settings.Agents, a => a is { Agent: "ea", Enabled: true });
        Assert.Equal(new QuietHoursDto(true, "21:30", "06:45"), settings.QuietHours);
        var router = factory.Services.GetRequiredService<NotificationRouter>();
        Assert.False(router.Options.AgentNotificationToggles[AgentId.Parse("research")]);
        Assert.Equal(new TimeOnly(21, 30), router.Options.QuietHours.StartsAt);
        Assert.True(File.Exists(Path.Combine(factory.StateRoot, "channels", "notification-settings.json")));

        await api.SaveNotificationSettingsAsync(new NotificationSettingsDto([new AgentNotificationToggleDto("research", true)], new QuietHoursDto(false, "22:00", "07:00")), Ct);
    }

    [Theory]
    [InlineData("nobody", "22:00", "07:00")]
    [InlineData("ea", "25:00", "07:00")]
    [InlineData("ea", "22:00", "7am")]
    public async Task Invalid_notification_settings_are_refused(string agent, string start, string end)
    {
        var client = factory.CreateDeviceClient(factory.PhoneToken);
        var body = new NotificationSettingsDto([new AgentNotificationToggleDto(agent, false)], new QuietHoursDto(true, start, end));

        var response = await client.PutAsJsonAsync(new Uri("/api/settings/notifications", UriKind.Relative), body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
