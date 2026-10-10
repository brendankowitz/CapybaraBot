using System.Net;
using System.Text;
using System.Text.Json;
using Capybara.Channels.Telegram;
using Capybara.Channels.WebPush;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Channels.Tests;

/// <summary>FR-CLI-03/04/06/07 and FR-SAF-07: factory payloads, Telegram polling, durable subscriptions, routing controls.</summary>
public sealed class ChannelAdapterTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private static readonly DateTimeOffset Noon = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static ApprovalRequest Approval(RiskLevel risk = RiskLevel.Medium) => new()
    {
        Id = "ap 1",
        Nonce = "abc123",
        Agent = Ea,
        SessionId = "s1",
        Tool = "email_send",
        ArgsHash = "hash",
        Preview = "To: boss@example.com — secret salary figures https://evil.example/x.png",
        Risk = risk,
        CreatedAt = Noon,
        ExpiresAt = Noon.AddMinutes(10),
    };

    // ---- NotificationFactory (FR-CLI-03) ----

    [Fact]
    public void Factory_approval_is_minimal_with_nonce_bound_actions()
    {
        var notification = NotificationFactory.Approval(Approval());

        Assert.Equal("approval needed: ea → email_send", notification.Title);
        Assert.Equal(string.Empty, notification.Body);
        Assert.Equal("/approvals", notification.DeepLinkPath);
        Assert.Equal(
            ["/api/approvals/ap%201/act?nonce=abc123&decision=allow-once", "/api/approvals/ap%201/act?nonce=abc123&decision=deny"],
            notification.Actions.Select(a => a.CallbackPath));
        Assert.DoesNotContain(notification.Actions, a => a.CallbackPath.Contains("always-allow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RiskLevel.Low, NotificationPriority.Low)]
    [InlineData(RiskLevel.Medium, NotificationPriority.Normal)]
    [InlineData(RiskLevel.High, NotificationPriority.High)]
    public void Factory_maps_risk_to_priority_so_low_risk_batches(RiskLevel risk, NotificationPriority expected) =>
        Assert.Equal(expected, NotificationFactory.Approval(Approval(risk)).Priority);

    [Fact]
    public async Task Factory_notifications_carry_no_preview_content_to_telegram()
    {
        var approval = Approval();
        var all = new[] { NotificationFactory.Approval(approval), NotificationFactory.NeedsInput(Ea), NotificationFactory.Finished(Ea) };
        var time = new FakeTimeProvider(Noon);
        var handler = new RecordingHandler();
        var telegram = new TelegramChannel(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, new TelegramOptions("bot-token", "123"));
        var router = new NotificationRouter([telegram], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        foreach (var notification in all)
        {
            await router.RouteAsync(notification, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, handler.Bodies.Count);
        foreach (var body in handler.Bodies)
        {
            var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
            Assert.DoesNotContain("boss@example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain("salary", text, StringComparison.Ordinal);
            Assert.DoesNotContain("email_send", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nonce", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        }

        Assert.Equal("Approval notification\n/approvals", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("text").GetString());
        Assert.Equal("NeedsInput notification\n/agents/ea", JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("text").GetString());
        Assert.Equal("ea finished\n/agents/ea", JsonDocument.Parse(handler.Bodies[2]).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Telegram_sends_plain_text_with_previews_off_and_absolute_deep_link()
    {
        var handler = new RecordingHandler();
        var telegram = new TelegramChannel(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") },
            new TelegramOptions("bot-token", "123") { DeepLinkBaseUrl = new Uri("https://capybara.tailnet/") });

        await telegram.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Equal("https://api.telegram.org/botbot-token/sendMessage", handler.Requests[0].RequestUri!.AbsoluteUri);
        var root = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.False(root.TryGetProperty("parse_mode", out _));
        Assert.True(root.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.True(root.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        Assert.Equal("ea finished\nhttps://capybara.tailnet/agents/ea", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Router_strips_remote_images_and_links_from_outbound_text_and_non_relative_paths()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        await router.RouteAsync(
            new Notification(NotificationKind.Finished, Ea, "see ![x](https://evil.example/p.png)", "visit https://evil.example/leak?d=secret", NotificationPriority.Normal, "//evil.example/", [new NotificationAction("go", "/\\evil.example")]),
            TestContext.Current.CancellationToken);

        var sent = Assert.Single(channel.Sent);
        Assert.DoesNotContain("evil.example", sent.Title + sent.Body, StringComparison.Ordinal);
        Assert.Equal("/", sent.DeepLinkPath);
        Assert.Empty(sent.Actions);
    }

    // ---- Telegram polling (FR-CLI-04) ----

    [Fact]
    public async Task Polling_advances_offset_drops_non_allowlisted_users_and_forwards_the_allowed_one()
    {
        var handler = new QueueHandler(
            """{"ok":true,"result":[{"update_id":10,"message":{"from":{"id":999},"text":"intruder"}},{"update_id":11,"message":{"from":{"id":123},"text":"hello"}},{"update_id":12,"edited_message":{"text":"x"}}]}""",
            """{"ok":true,"result":[]}""");
        var sink = new RecordingInboundSink();
        var service = NewPoller(handler, sink);

        Assert.Equal(3, await service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(13, service.Offset);
        Assert.Equal(0, await service.PollOnceAsync(TestContext.Current.CancellationToken));

        var received = Assert.Single(sink.Messages);
        Assert.Equal(("123", "hello"), received);
        var first = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal(0, first.GetProperty("offset").GetInt64());
        Assert.Equal(30, first.GetProperty("timeout").GetInt32());
        Assert.Equal(13, JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("offset").GetInt64());
        Assert.Equal("https://api.telegram.org/botbot-token/getUpdates", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Polling_skips_an_update_whose_sink_throws_instead_of_wedging()
    {
        var handler = new QueueHandler("""{"ok":true,"result":[{"update_id":5,"message":{"from":{"id":123},"text":"boom"}},{"update_id":6,"message":{"from":{"id":123},"text":"ok"}}]}""");
        var sink = new RecordingInboundSink { ThrowOn = "boom" };
        var service = NewPoller(handler, sink);

        await service.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, service.Offset);
        Assert.Equal("ok", Assert.Single(sink.Messages).Text);
    }

    [Fact]
    public async Task Polling_rejects_a_response_without_a_result_array_and_keeps_the_offset()
    {
        var service = NewPoller(new QueueHandler("""{"ok":false,"description":"Unauthorized"}"""), new RecordingInboundSink());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, service.Offset);
    }

    [Fact]
    public async Task Polling_service_backs_off_after_failure_then_delivers()
    {
        var time = new FakeTimeProvider(Noon);
        var handler = new QueueHandler(HttpStatusCode.BadGateway, """{"ok":true,"result":[{"update_id":1,"message":{"from":{"id":123},"text":"hi"}}]}""") { BlockWhenEmpty = true };
        var sink = new RecordingInboundSink();
        var service = NewPoller(handler, sink, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hi", Assert.Single(sink.Messages).Text);
        Assert.Equal(2, service.Offset);
    }

    // ---- Durable subscriptions (FR-CLI-06) ----

    [Fact]
    public void Subscriptions_persist_across_instances_and_removal_is_durable()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "state", "subs.json");
        var store = new WebPushSubscriptionStore(path);
        store.Upsert(new WebPushSubscription("ios", new Uri("https://push.example.test/1"), "p256", "auth"));
        store.Upsert(new WebPushSubscription("mac", new Uri("https://push.example.test/2"), "p256b", "authb"));
        store.Upsert(new WebPushSubscription("ios", new Uri("https://push.example.test/1b"), "p256c", "authc"));

        var reloaded = new WebPushSubscriptionStore(path);
        Assert.Equal(2, reloaded.Subscriptions.Count);
        Assert.Equal("https://push.example.test/1b", reloaded.Subscriptions.Single(s => s.DeviceId == "ios").Endpoint.AbsoluteUri);

        Assert.True(reloaded.Remove("ios"));
        Assert.False(reloaded.Remove("ios"));
        var again = new WebPushSubscriptionStore(path);
        Assert.Equal("mac", Assert.Single(again.Subscriptions).DeviceId);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupt_subscription_file_fails_loudly()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "subs.json");
        File.WriteAllText(path, "{ not json");

        Assert.ThrowsAny<JsonException>(() => new WebPushSubscriptionStore(path));
    }

    [Fact]
    public async Task Web_push_without_subscriptions_throws_so_the_router_falls_back()
    {
        var channel = NewWebPush(new WebPushSubscriptionStore(), new RecordingHandler(), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Web_push_prunes_gone_subscriptions_and_still_succeeds_for_the_rest()
    {
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("gone", new Uri("https://push.example.test/gone"), keys.Public, keys.Auth));
        store.Upsert(new WebPushSubscription("ok", new Uri("https://push.example.test/ok"), keys.Public, keys.Auth));
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("gone", StringComparison.Ordinal) ? HttpStatusCode.Gone : HttpStatusCode.Created);
        var channel = NewWebPush(store, handler, out _);

        await channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Equal("ok", Assert.Single(store.Subscriptions).DeviceId);
    }

    [Fact]
    public async Task Web_push_throws_when_every_delivery_fails()
    {
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("a", new Uri("https://push.example.test/a"), keys.Public, keys.Auth));
        var channel = NewWebPush(store, new RecordingHandler(_ => HttpStatusCode.InternalServerError), out _);

        await Assert.ThrowsAsync<HttpRequestException>(() => channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken).AsTask());
        Assert.Single(store.Subscriptions);
    }

    // ---- Routing controls (FR-CLI-07) ----

    [Fact]
    public void FromAgents_maps_notification_flags_to_toggles()
    {
        var options = ChannelOptions.FromAgents(
        [
            new AgentDefinition { Id = Ea, Name = "EA", Job = "assist" },
            new AgentDefinition { Id = AgentId.Parse("code"), Name = "Code", Job = "code", Notifications = false },
        ]);

        Assert.True(options.AgentNotificationToggles[Ea]);
        Assert.False(options.AgentNotificationToggles[AgentId.Parse("code")]);
    }

    [Fact]
    public async Task Router_honours_per_agent_toggle_from_agent_definitions_for_every_kind()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new StubChannel("ntfy");
        var options = ChannelOptions.FromAgents([new AgentDefinition { Id = Ea, Name = "EA", Job = "assist", Notifications = false }]);
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), options, time);
        var ct = TestContext.Current.CancellationToken;

        await router.RouteAsync(NotificationFactory.Finished(Ea), ct);
        await router.RouteAsync(NotificationFactory.NeedsInput(Ea), ct);
        await router.RouteAsync(NotificationFactory.Approval(Approval(RiskLevel.High)), ct);
        await router.RouteAsync(NotificationFactory.Finished(AgentId.Parse("research")), ct);

        Assert.Equal("research finished", Assert.Single(channel.Sent).Title);
    }

    [Theory]
    [InlineData(21, 59, true)]
    [InlineData(22, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(6, 59, false)]
    [InlineData(7, 0, true)]
    public async Task Router_quiet_hours_window_wraps_midnight_with_inclusive_start_and_exclusive_end(int hour, int minute, bool delivered)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, hour, minute, 0, TimeSpan.Zero));
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions
        {
            QuietHours = new QuietHoursOptions { Enabled = true, StartsAt = new TimeOnly(22, 0), EndsAt = new TimeOnly(7, 0) },
        }, time);

        await router.RouteAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Equal(delivered, channel.Sent.Count == 1);
    }

    [Fact]
    public async Task Router_quiet_hours_let_critical_alerts_through_and_can_be_disabled()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.Zero));
        var channel = new StubChannel("ntfy");
        var quiet = new QuietHoursOptions { Enabled = true };
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions { QuietHours = quiet }, time);
        var ct = TestContext.Current.CancellationToken;

        await router.RouteAsync(new Notification(NotificationKind.Alert, null, "warn", "b", NotificationPriority.High, "/alerts"), ct);
        await router.RouteAsync(new Notification(NotificationKind.Alert, null, "crit", "b", NotificationPriority.Critical, "/alerts"), ct);
        Assert.Equal("crit", Assert.Single(channel.Sent).Title);

        var open = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions { QuietHours = quiet with { Enabled = false } }, time);
        await open.RouteAsync(NotificationFactory.Finished(Ea), ct);
        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task Router_suppresses_non_urgent_push_while_pwa_is_focused_until_the_heartbeat_expires()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new StubChannel("ntfy");
        var presence = new PresenceTracker(time, TimeSpan.FromSeconds(45));
        var router = new NotificationRouter([channel], presence, new InMemoryAlertSink(), new ChannelOptions(), time);
        var ct = TestContext.Current.CancellationToken;

        presence.SetFocused("phone", true);
        await router.RouteAsync(NotificationFactory.Finished(Ea), ct);
        await router.RouteAsync(NotificationFactory.NeedsInput(Ea), ct);
        Assert.Empty(channel.Sent);

        presence.SetFocused("phone", false);
        await router.RouteAsync(NotificationFactory.Finished(Ea), ct);
        Assert.Single(channel.Sent);

        presence.SetFocused("phone", true);
        time.Advance(TimeSpan.FromSeconds(46));
        await router.RouteAsync(NotificationFactory.NeedsInput(Ea), ct);
        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task Router_batch_timer_rearms_for_each_window()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions { LowRiskBatchWindow = TimeSpan.FromSeconds(5) }, time);
        var ct = TestContext.Current.CancellationToken;
        var low = NotificationFactory.Approval(Approval(RiskLevel.Low));

        await router.RouteAsync(low, ct);
        time.Advance(TimeSpan.FromSeconds(6));
        await WaitForAsync(() => channel.Sent.Count == 1);

        await router.RouteAsync(low, ct);
        time.Advance(TimeSpan.FromSeconds(6));
        await WaitForAsync(() => channel.Sent.Count == 2);
    }

    // ---- helpers ----

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }

    private static TelegramPollingService NewPoller(QueueHandler handler, RecordingInboundSink sink, TimeProvider? time = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") },
        new TelegramOptions("bot-token", "123") { AllowedUserId = "123" },
        new TelegramUpdateHandler(new ConversationalChannelGuard(new Dictionary<string, string> { ["telegram"] = "123" }), sink),
        time ?? TimeProvider.System);

    private static (string Public, string Auth) NewSubscriptionKeys()
    {
        using var subscriber = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (
            WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(subscriber.ExportParameters(false))),
            WebPushCrypto.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
    }

    private static WebPushChannel NewWebPush(WebPushSubscriptionStore store, RecordingHandler handler, out WebPushVapidOptions vapidOptions)
    {
        using var vapid = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = vapid.ExportParameters(true);
        vapidOptions = new WebPushVapidOptions(
            "mailto:owner@example.test",
            WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(new System.Security.Cryptography.ECParameters { Q = p.Q })),
            WebPushCrypto.Base64UrlEncode([.. p.D!, .. p.Q.X!, .. p.Q.Y!]));
        return new WebPushChannel(new HttpClient(handler), store, vapidOptions, TimeProvider.System);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "capybara-channels-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpStatusCode>? status = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(request);
            return new HttpResponseMessage(status?.Invoke(request) ?? HttpStatusCode.OK);
        }
    }

    /// <summary>Replays canned responses in order; each entry is a JSON body or an error status code.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<object> _responses;

        public QueueHandler(params object[] responses) => _responses = new Queue<object>(responses);

        public bool BlockWhenEmpty { get; init; }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(request);
            if (!_responses.TryDequeue(out var next))
            {
                if (BlockWhenEmpty)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                next = """{"ok":true,"result":[]}""";
            }

            return next switch
            {
                HttpStatusCode code => new HttpResponseMessage(code),
                string json => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
                _ => throw new InvalidOperationException(),
            };
        }
    }

    private sealed class StubChannel(string name) : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];

        public string Name => name;

        public bool SupportsSensitiveContent => true;

        public ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            Sent.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingInboundSink : IInboundMessageSink
    {
        public List<(string UserId, string Text)> Messages { get; } = [];

        public string? ThrowOn { get; init; }

        public ValueTask ReceiveAsync(string channel, string userId, string text, CancellationToken cancellationToken = default)
        {
            if (text == ThrowOn)
            {
                throw new InvalidOperationException("sink failed");
            }

            Messages.Add((userId, text));
            return ValueTask.CompletedTask;
        }
    }
}
