using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Channels.Ntfy;
using Capybara.Channels.Telegram;
using Capybara.Channels.WebPush;
using Capybara.Contracts.Agents;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Channels.Tests;

public sealed class ChannelsTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    [Fact]
    public async Task Ntfy_sets_headers_actions_click_and_markdown_disabled()
    {
        var handler = new RecordingHandler();
        var channel = new NtfyChannel(new HttpClient(handler), new NtfyOptions(new Uri("https://ntfy.tailnet/"), "capybara") { AccessToken = "runtime-token" });
        var notification = new Notification(
            NotificationKind.Approval,
            Ea,
            "approval needed: ea → send email",
            "Open the approval card.",
            NotificationPriority.High,
            "/approvals/abc",
            [new NotificationAction("Allow once", "/api/approvals/abc?nonce=n&decision=allow-once")]);

        await channel.SendAsync(notification, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://ntfy.tailnet/capybara"), request.RequestUri);
        Assert.Equal("approval needed: ea → send email", Header(request, "Title"));
        Assert.Equal("4", Header(request, "Priority"));
        Assert.Equal("warning", Header(request, "Tags"));
        Assert.Equal("/approvals/abc", Header(request, "Click"));
        Assert.Equal("no", Header(request, "X-Markdown"));
        Assert.Contains("http, Allow once, /api/approvals/abc?nonce=n&decision=allow-once, method=POST, clear=true", Header(request, "Actions"), StringComparison.Ordinal);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Telegram_disables_previews_and_omits_sensitive_text()
    {
        var handler = new RecordingHandler();
        var channel = new TelegramChannel(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, new TelegramOptions("bot-token", "123"));
        var notification = new Notification(NotificationKind.NeedsInput, Ea, "secret title", "do not leak sensitive body", NotificationPriority.High, "/questions/q1", IsSensitive: true);

        await channel.SendAsync(notification, TestContext.Current.CancellationToken);

        var json = Encoding.UTF8.GetString(handler.Bodies[0]);
        Assert.Contains("disable_web_page_preview", json, StringComparison.Ordinal);
        Assert.Contains("is_disabled", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret title", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive body", json, StringComparison.Ordinal);
        Assert.Contains("/questions/q1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_applies_controls_batches_fallback_and_all_fail_alerts()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var alerts = new InMemoryAlertSink();
        var primary = new StubChannel("ntfy") { Fail = true };
        var fallback = new StubChannel("telegram") { SupportsSensitive = false };
        var presence = new PresenceTracker(time, TimeSpan.FromSeconds(30));
        var router = new NotificationRouter([primary, fallback], presence, alerts, new ChannelOptions
        {
            AgentNotificationToggles = new Dictionary<AgentId, bool> { [AgentId.Parse("code")] = false },
            LowRiskBatchWindow = TimeSpan.FromSeconds(5),
        }, time);
        var ct = TestContext.Current.CancellationToken;

        await router.RouteAsync(new Notification(NotificationKind.Finished, AgentId.Parse("code"), "done", "body", NotificationPriority.Normal, "/runs/1"), ct);
        Assert.Empty(primary.Sent);

        presence.SetFocused("phone", true);
        await router.RouteAsync(new Notification(NotificationKind.Finished, Ea, "done", "body", NotificationPriority.Normal, "/runs/2"), ct);
        Assert.Empty(primary.Sent);

        await router.RouteAsync(new Notification(NotificationKind.Approval, Ea, "approval", "secret", NotificationPriority.High, "/approvals/1", IsSensitive: true), ct);
        Assert.Single(fallback.Sent);
        Assert.Equal("Approval notification", fallback.Sent[0].Title);
        Assert.Equal(string.Empty, fallback.Sent[0].Body);

        await router.RouteAsync(new Notification(NotificationKind.Approval, Ea, "low 1", "body", NotificationPriority.Low, "/approvals/2"), ct);
        await router.RouteAsync(new Notification(NotificationKind.Approval, Ea, "low 2", "body", NotificationPriority.Low, "/approvals/3"), ct);
        Assert.Single(fallback.Sent);
        await router.FlushBatchesAsync(ct);
        Assert.Equal(2, fallback.Sent.Count);
        Assert.Contains("2 low-risk approvals", fallback.Sent[1].Title, StringComparison.Ordinal);

        var failingRouter = new NotificationRouter([new StubChannel("only") { Fail = true }], presence, alerts, new ChannelOptions(), time);
        await failingRouter.RouteAsync(new Notification(NotificationKind.Alert, Ea, "alert", "body", NotificationPriority.Critical, "/alerts"), ct);
        Assert.Contains(alerts.Recent, a => a.Kind == "notification.delivery_failed");
    }

    [Fact]
    public async Task Router_respects_quiet_hours_for_non_bypassed_notifications()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.Zero));
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions
        {
            QuietHours = new QuietHoursOptions { Enabled = true, StartsAt = new TimeOnly(22, 0), EndsAt = new TimeOnly(7, 0) },
        }, time);

        await router.RouteAsync(new Notification(NotificationKind.Finished, Ea, "done", "body", NotificationPriority.Normal, "/runs/1"), TestContext.Current.CancellationToken);
        await router.RouteAsync(new Notification(NotificationKind.Approval, Ea, "approval", "body", NotificationPriority.High, "/approvals/1"), TestContext.Current.CancellationToken);

        Assert.Single(channel.Sent);
        Assert.Equal(NotificationKind.Approval, channel.Sent[0].Kind);
    }

    [Fact]
    public async Task Guard_drops_other_users_silently_and_handler_forwards_allowed_text()
    {
        var sink = new RecordingInboundSink();
        var guard = new ConversationalChannelGuard(new Dictionary<string, string> { ["telegram"] = "123" });
        var handler = new TelegramUpdateHandler(guard, sink);

        await handler.HandleAsync(JsonDocument.Parse("{\"message\":{\"from\":{\"id\":999},\"text\":\"bad\"}}"), TestContext.Current.CancellationToken);
        await handler.HandleAsync(JsonDocument.Parse("{\"message\":{\"from\":{\"id\":123},\"text\":\"hello\"}}"), TestContext.Current.CancellationToken);

        var received = Assert.Single(sink.Messages);
        Assert.Equal("hello", received.Text);
    }

    [Fact]
    public async Task Phone_escalation_returns_answer_or_null_on_timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);
        var escalation = new PhoneUserEscalation(router, time);
        var ct = TestContext.Current.CancellationToken;

        var ask = escalation.AskAsync("q1", Ea, "need input", "question", TimeSpan.FromMinutes(1), ct).AsTask();
        Assert.True(escalation.SubmitAnswer("q1", "answer"));
        Assert.Equal("answer", await ask);

        var timeout = escalation.AskAsync("q2", Ea, "need input", "question", TimeSpan.FromSeconds(5), ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await timeout);
    }

    [Fact]
    public async Task Web_push_vapid_jwt_and_aes128gcm_round_trip()
    {
        using var subscriber = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscriberPublic = WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(subscriber.ExportParameters(false)));
        var authSecret = WebPushCrypto.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var vapidParams = vapid.ExportParameters(true);
        var privateKey = WebPushCrypto.Base64UrlEncode([.. vapidParams.D!, .. vapidParams.Q.X!, .. vapidParams.Q.Y!]);
        var publicKey = WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(new ECParameters { Q = vapidParams.Q }));
        var store = new WebPushSubscriptionStore();
        store.Upsert(new WebPushSubscription("ios", new Uri("https://push.example.test/send/1"), subscriberPublic, authSecret));
        var handler = new RecordingHandler();
        var channel = new WebPushChannel(new HttpClient(handler), store, new WebPushVapidOptions("mailto:owner@example.test", publicKey, privateKey), TimeProvider.System);

        await channel.SendAsync(new Notification(NotificationKind.Finished, Ea, "finished", "open", NotificationPriority.Normal, "/runs/1"), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        var auth = Header(request, "Authorization");
        Assert.StartsWith("vapid t=", auth, StringComparison.Ordinal);
        Assert.Contains(", k=", auth, StringComparison.Ordinal);
        var token = auth[8..auth.IndexOf(", k=", StringComparison.Ordinal)];
        Assert.Equal(3, token.Split('.').Length);
        var body = handler.Bodies[0];
        var encrypted = WebPushEncryptionResult.FromAes128GcmBody(body);
        var decrypted = WebPushCrypto.Decrypt(encrypted, subscriber, authSecret, subscriberPublic);
        var json = Encoding.UTF8.GetString(decrypted);
        Assert.Contains("finished", json, StringComparison.Ordinal);
        Assert.Contains("/runs/1", json, StringComparison.Ordinal);
    }

    private static string Header(HttpRequestMessage request, string name) => string.Join(",", request.Headers.GetValues(name));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Bodies.Add(body);
                request.Content = new ByteArrayContent(body);
            }
            else
            {
                Bodies.Add([]);
            }

            Requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubChannel(string name) : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];

        public bool Fail { get; init; }

        public bool SupportsSensitive { get; init; } = true;

        public string Name => name;

        public bool SupportsSensitiveContent => SupportsSensitive;

        public ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new HttpRequestException("boom");
            }

            Sent.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingInboundSink : IInboundMessageSink
    {
        public List<(string UserId, string Text)> Messages { get; } = [];

        public ValueTask ReceiveAsync(string channel, string userId, string text, CancellationToken cancellationToken = default)
        {
            Messages.Add((userId, text));
            return ValueTask.CompletedTask;
        }
    }
}
