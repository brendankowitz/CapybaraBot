using System.Net;
using System.Text;
using System.Text.Json;
using Capybara.Channels.Ntfy;
using Capybara.Channels.DependencyInjection;
using Capybara.Channels.Telegram;
using Capybara.Channels.WebPush;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.Equal(("123", "hello", "telegram:11"), received);
        var first = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        Assert.Equal(0, first.GetProperty("offset").GetInt64());
        Assert.Equal(30, first.GetProperty("timeout").GetInt32());
        Assert.Equal(13, JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("offset").GetInt64());
        Assert.Equal("https://api.telegram.org/botbot-token/getUpdates", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Polling_redelivers_an_update_whose_sink_throws_and_never_skips_ahead()
    {
        var update5 = """{"update_id":5,"message":{"from":{"id":123},"text":"boom"}}""";
        var update6 = """{"update_id":6,"message":{"from":{"id":123},"text":"ok"}}""";
        var handler = new QueueHandler($$"""{"ok":true,"result":[{{update5}},{{update6}}]}""", $$"""{"ok":true,"result":[{{update5}},{{update6}}]}""");
        var sink = new RecordingInboundSink { ThrowOn = "boom", ThrowCount = 1 };
        var service = NewPoller(handler, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, service.Offset);
        Assert.Empty(sink.Messages);

        Assert.Equal(2, await service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(7, service.Offset);
        Assert.Equal(["boom", "ok"], sink.Messages.Select(m => m.Text));
        Assert.Equal(["telegram:5", "telegram:6"], sink.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Handler_requires_an_update_id_for_the_idempotency_key_but_not_for_dropped_messages()
    {
        var sink = new RecordingInboundSink();
        var handler = new TelegramUpdateHandler(new ConversationalChannelGuard(new Dictionary<string, string> { ["telegram"] = "123" }), sink);

        await handler.HandleAsync(JsonDocument.Parse("""{"message":{"from":{"id":999},"text":"intruder"}}"""), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(JsonDocument.Parse("""{"message":{"from":{"id":123},"text":"x"}}"""), TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task Polling_skips_a_poison_update_only_after_the_window_and_minimum_attempts_and_raises_an_alert()
    {
        var update = """{"update_id":5,"message":{"from":{"id":123},"text":"boom"}}""";
        var body = $$"""{"ok":true,"result":[{{update}}]}""";
        var handler = new QueueHandler(Enumerable.Repeat<object>(body, 11).Append("""{"ok":true,"result":[]}""").ToArray());
        var sink = new RecordingInboundSink { ThrowOn = "boom" };
        var alerts = new InMemoryAlertSink();
        var time = new FakeTimeProvider(Noon);
        var service = NewPoller(handler, sink, time, alerts);

        // Retries at the service's backoff cadence (<= MaxRetryDelay): ten attempts over nine minutes never skip.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
            time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(0, service.Offset);
        Assert.Empty(alerts.Recent);

        // The eleventh attempt lands ten minutes after the first failure: now it is poison.
        Assert.Equal(1, await service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, service.Offset);
        var alert = Assert.Single(alerts.Recent);
        Assert.Equal("telegram.inbound_skipped", alert.Kind);
        Assert.DoesNotContain("boom", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_network_outage_between_failures_restarts_the_poison_window()
    {
        var failing = """{"ok":true,"result":[{"update_id":5,"message":{"from":{"id":123},"text":"boom"}}]}""";
        var handler = new QueueHandler(failing, HttpStatusCode.BadGateway, failing, failing, failing, failing, failing);
        var alerts = new InMemoryAlertSink();
        var time = new FakeTimeProvider(Noon);
        var service = NewPoller(handler, new RecordingInboundSink { ThrowOn = "boom" }, time, alerts);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollOnceAsync(ct));
        time.Advance(TimeSpan.FromMinutes(11));

        // After recovery the inbox is down for only seconds: five quick failures must not drop the message.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(ct));
            time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(0, service.Offset);
        Assert.Empty(alerts.Recent);
    }

    [Fact]
    public async Task Polling_skips_an_update_without_update_id_but_still_delivers_the_rest()
    {
        var handler = new QueueHandler("""{"ok":true,"result":[{"message":{"from":{"id":123},"text":"no id"}},{"update_id":8,"message":{"from":{"id":123},"text":"ok"}}]}""");
        var sink = new RecordingInboundSink();
        var service = NewPoller(handler, sink);

        Assert.Equal(1, await service.PollOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(9, service.Offset);
        Assert.Equal("ok", Assert.Single(sink.Messages).Text);
    }

    [Fact]
    public async Task Polling_fails_loudly_instead_of_hot_looping_when_only_unidentifiable_updates_arrive()
    {
        var service = NewPoller(new QueueHandler("""{"ok":true,"result":[{"update_id":"x","message":{"text":"bad"}}]}"""), new RecordingInboundSink());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, service.Offset);
    }

    [Fact]
    public async Task Polling_does_not_skip_when_the_window_elapsed_but_too_few_attempts_were_made()
    {
        var body = """{"ok":true,"result":[{"update_id":5,"message":{"from":{"id":123},"text":"boom"}}]}""";
        var handler = new QueueHandler(body, body);
        var alerts = new InMemoryAlertSink();
        var time = new FakeTimeProvider(Noon);
        var service = NewPoller(handler, new RecordingInboundSink { ThrowOn = "boom" }, time, alerts);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, service.Offset);
        Assert.Empty(alerts.Recent);
    }

    [Fact]
    public async Task Polling_advances_even_when_the_alert_sink_fails_while_skipping_poison()
    {
        var body = """{"ok":true,"result":[{"update_id":5,"message":{"from":{"id":123},"text":"boom"}}]}""";
        var handler = new QueueHandler(Enumerable.Repeat<object>(body, TelegramPollingService.MaxDeliveryAttempts).ToArray());
        var time = new FakeTimeProvider(Noon);
        var service = NewPoller(handler, new RecordingInboundSink { ThrowOn = "boom" }, time, new ThrowingAlertSink(), new TelegramOptions("bot-token", "123") { AllowedUserId = "123", PoisonMessageWindow = TimeSpan.Zero });

        for (var attempt = 1; attempt < TelegramPollingService.MaxDeliveryAttempts; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollOnceAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, await service.PollOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, service.Offset);
    }

    [Fact]
    public async Task Polling_service_survives_a_transport_timeout_and_keeps_polling()
    {
        var time = new FakeTimeProvider(Noon);
        var handler = new QueueHandler(new TaskCanceledException("HttpClient.Timeout"), """{"ok":true,"result":[{"update_id":1,"message":{"from":{"id":123},"text":"hi"}}]}""") { BlockWhenEmpty = true };
        var sink = new RecordingInboundSink();
        var service = NewPoller(handler, sink, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal("hi", Assert.Single(sink.Messages).Text);
        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Polling_service_stops_cleanly_on_real_shutdown_during_a_blocked_poll()
    {
        var handler = new QueueHandler { BlockWhenEmpty = true };
        var service = NewPoller(handler, new RecordingInboundSink());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => handler.Requests.Count == 1);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
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

    // ---- Review fixes ----

    [Fact]
    public async Task Batched_low_risk_approvals_never_leak_agent_or_tool_names_to_telegram()
    {
        var time = new FakeTimeProvider(Noon);
        var handler = new RecordingHandler();
        var telegram = new TelegramChannel(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, new TelegramOptions("bot-token", "123"));
        var router = new NotificationRouter([telegram], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);
        var ct = TestContext.Current.CancellationToken;
        var nonSensitiveLeaky = new Notification(NotificationKind.Approval, Ea, "approval needed: ea → email_send", "ea wants email_send", NotificationPriority.Low, "/approvals");

        var leaky = new[] { nonSensitiveLeaky };
        var mixed = new[] { NotificationFactory.Approval(Approval(RiskLevel.Low)), nonSensitiveLeaky };
        var sensitiveOnly = new[] { NotificationFactory.Approval(Approval(RiskLevel.Low)) };

        foreach (var batch in new[] { sensitiveOnly, leaky, mixed })
        {
            handler.Bodies.Clear();
            foreach (var item in batch)
            {
                await router.RouteAsync(item, ct);
            }

            await router.FlushBatchesAsync(ct);

            var text = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement.GetProperty("text").GetString()!;
            Assert.DoesNotContain("email_send", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ea →", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Batch_summary_uses_fixed_text_and_has_no_actions()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new StubChannel("ntfy");
        var router = new NotificationRouter([channel], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);
        var ct = TestContext.Current.CancellationToken;

        await router.RouteAsync(NotificationFactory.Approval(Approval(RiskLevel.Low)), ct);
        await router.FlushBatchesAsync(ct);

        var summary = Assert.Single(channel.Sent);
        Assert.Equal("1 approval needs review", summary.Title);
        Assert.Equal("Open approvals in the PWA.", summary.Body);
        Assert.Null(summary.Agent);
        Assert.Empty(summary.Actions);
    }

    [Fact]
    public async Task Ntfy_without_sensitive_support_gets_no_action_buttons_or_tool_name()
    {
        var time = new FakeTimeProvider(Noon);
        var handler = new RecordingHandler();
        var ntfy = new NtfyChannel(new HttpClient(handler), new NtfyOptions(new Uri("https://ntfy.sh/"), "capybara") { GatewayBaseUrl = new Uri("https://capybara.tailnet/") });
        var router = new NotificationRouter([ntfy], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        await router.RouteAsync(NotificationFactory.Approval(Approval(RiskLevel.High)), TestContext.Current.CancellationToken);

        var raw = Encoding.UTF8.GetString(Assert.Single(handler.Bodies));
        using var json = JsonDocument.Parse(raw);
        Assert.False(json.RootElement.TryGetProperty("actions", out _));
        Assert.Equal("Approval notification", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("https://capybara.tailnet/approvals", json.RootElement.GetProperty("click").GetString());
        Assert.DoesNotContain("email_send", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ntfy_with_sensitive_support_keeps_nonce_bound_buttons()
    {
        var time = new FakeTimeProvider(Noon);
        var handler = new RecordingHandler();
        var ntfy = new NtfyChannel(new HttpClient(handler), new NtfyOptions(new Uri("https://ntfy.tailnet/"), "capybara") { GatewayBaseUrl = new Uri("https://capybara.tailnet/"), SupportsSensitiveContent = true });
        var router = new NotificationRouter([ntfy], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        await router.RouteAsync(NotificationFactory.Approval(Approval(RiskLevel.High)), TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("approval needed: ea → email_send", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task Telegram_http_client_logging_never_records_the_bot_token()
    {
        const string token = "123456:SECRET-TOKEN";
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddCapybaraTelegramChannel(new TelegramOptions(token, "123"));
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => new ConstantHandler()));
        await using var provider = services.BuildServiceProvider();

        await provider.GetServices<INotificationChannel>().Single().SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Messages, m => m.Contains("SECRET-TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Web_push_delivers_to_healthy_devices_when_others_fail_and_logs_the_failures()
    {
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("badkey", new Uri("https://push.example.test/badkey"), "AAAA", keys.Auth));
        store.Upsert(new WebPushSubscription("flaky", new Uri("https://push.example.test/flaky"), keys.Public, keys.Auth));
        store.Upsert(new WebPushSubscription("gone", new Uri("https://push.example.test/gone"), keys.Public, keys.Auth));
        store.Upsert(new WebPushSubscription("ok", new Uri("https://push.example.test/ok"), keys.Public, keys.Auth));
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/flaky" => HttpStatusCode.InternalServerError,
            "/gone" => HttpStatusCode.Gone,
            _ => HttpStatusCode.Created,
        });
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var channel = NewWebPush(store, handler, out _, loggerFactory.CreateLogger<WebPushChannel>());

        await channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Equal(["badkey", "flaky", "ok"], store.Subscriptions.Select(s => s.DeviceId).Order());        Assert.DoesNotContain(store.Subscriptions, s => s.DeviceId == "gone");
        Assert.Contains(logs.Messages, m => m.Contains("badkey", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains("flaky", StringComparison.Ordinal) && m.Contains("500", StringComparison.Ordinal));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Web_push_timeout_on_one_device_does_not_abort_delivery_to_the_others()
    {
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("slow", new Uri("https://push.example.test/slow"), keys.Public, keys.Auth));
        store.Upsert(new WebPushSubscription("ok", new Uri("https://push.example.test/ok"), keys.Public, keys.Auth));
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/slow"
            ? throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException())
            : HttpStatusCode.Created);
        var channel = NewWebPush(store, handler, out _);

        await channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, store.Subscriptions.Count);
    }

    [Fact]
    public async Task Router_falls_back_to_the_next_channel_when_every_web_push_device_times_out()
    {
        var time = new FakeTimeProvider(Noon);
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("slow", new Uri("https://push.example.test/slow"), keys.Public, keys.Auth));
        var webPush = NewWebPush(store, new RecordingHandler(_ => throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException())), out _);
        var fallback = new StubChannel("ntfy");
        var router = new NotificationRouter([webPush, fallback], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        await router.RouteAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Single(fallback.Sent);
    }

    [Fact]
    public async Task Router_falls_back_when_a_channel_times_out_but_propagates_real_cancellation()
    {
        var time = new FakeTimeProvider(Noon);
        var fallback = new StubChannel("ntfy");
        var timingOut = new StubChannel("web-push") { Throw = new TaskCanceledException("HttpClient.Timeout") };
        var router = new NotificationRouter([timingOut, fallback], new PresenceTracker(time, TimeSpan.FromSeconds(30)), new InMemoryAlertSink(), new ChannelOptions(), time);

        await router.RouteAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);
        Assert.Single(fallback.Sent);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.RouteAsync(NotificationFactory.Finished(Ea), cts.Token).AsTask());
        Assert.Single(fallback.Sent);
    }

    [Fact]
    public async Task Web_push_throws_when_no_device_succeeds_even_if_one_failure_is_a_crypto_error()
    {
        var store = new WebPushSubscriptionStore();
        var keys = NewSubscriptionKeys();
        store.Upsert(new WebPushSubscription("badkey", new Uri("https://push.example.test/badkey"), "AAAA", keys.Auth));
        store.Upsert(new WebPushSubscription("flaky", new Uri("https://push.example.test/flaky"), keys.Public, keys.Auth));
        var channel = NewWebPush(store, new RecordingHandler(_ => HttpStatusCode.InternalServerError), out _);

        await Assert.ThrowsAsync<HttpRequestException>(() => channel.SendAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Di_router_logs_channel_failures_through_the_host_logger()
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddSingleton<IAlertSink, InMemoryAlertSink>();
        services.AddSingleton<INotificationChannel>(new StubChannel("flaky-ntfy") { Fail = true });
        services.AddSingleton<INotificationChannel>(new StubChannel("backup"));
        services.AddCapybaraChannels(new ChannelOptions());
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<NotificationRouter>().RouteAsync(NotificationFactory.Finished(Ea), TestContext.Current.CancellationToken);

        Assert.Contains(logs.Messages, m => m.Contains("flaky-ntfy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Router_lets_approvals_through_pwa_focus_suppression_but_not_other_kinds()
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

        await router.RouteAsync(NotificationFactory.Approval(Approval(RiskLevel.High)), ct);
        Assert.Equal(NotificationKind.Approval, Assert.Single(channel.Sent).Kind);
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

    private static TelegramPollingService NewPoller(QueueHandler handler, RecordingInboundSink sink, TimeProvider? time = null, IAlertSink? alerts = null, TelegramOptions? options = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") },
        options ?? new TelegramOptions("bot-token", "123") { AllowedUserId = "123" },
        new TelegramUpdateHandler(new ConversationalChannelGuard(new Dictionary<string, string> { ["telegram"] = "123" }), sink),
        time ?? TimeProvider.System,
        alerts: alerts);

    private static (string Public, string Auth) NewSubscriptionKeys()
    {
        using var subscriber = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (
            WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(subscriber.ExportParameters(false))),
            WebPushCrypto.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
    }

    private static WebPushChannel NewWebPush(WebPushSubscriptionStore store, RecordingHandler handler, out WebPushVapidOptions vapidOptions, ILogger<WebPushChannel>? logger = null)
    {
        using var vapid = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = vapid.ExportParameters(true);
        vapidOptions = new WebPushVapidOptions(
            "mailto:owner@example.test",
            WebPushCrypto.Base64UrlEncode(WebPushCrypto.ExportUncompressed(new System.Security.Cryptography.ECParameters { Q = p.Q })),
            WebPushCrypto.Base64UrlEncode([.. p.D!, .. p.Q.X!, .. p.Q.Y!]));
        return new WebPushChannel(new HttpClient(handler), store, vapidOptions, TimeProvider.System, logger);
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
                Exception ex => throw ex,
                HttpStatusCode code => new HttpResponseMessage(code),
                string json => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
                _ => throw new InvalidOperationException(),
            };
        }
    }

    private sealed class StubChannel(string name) : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];

        public bool Fail { get; init; }

        public Exception? Throw { get; init; }

        public string Name => name;

        public bool SupportsSensitiveContent => true;

        public ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Fail)
            {
                throw new HttpRequestException("boom");
            }

            Sent.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class ConstantHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingAlertSink : IAlertSink
    {
        public ValueTask PublishAsync(Alert alert, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("alert sink down");
    }

    private sealed class RecordingInboundSink : IInboundMessageSink
    {
        public List<(string UserId, string Text, string MessageId)> Messages { get; } = [];

        public string? ThrowOn { get; init; }

        public int ThrowCount { get; set; } = int.MaxValue;

        public ValueTask ReceiveAsync(string channel, string userId, string text, string messageId, CancellationToken cancellationToken = default)
        {
            if (text == ThrowOn && ThrowCount-- > 0)
            {
                throw new InvalidOperationException("sink failed");
            }

            Messages.Add((userId, text, messageId));
            return ValueTask.CompletedTask;
        }
    }
}
