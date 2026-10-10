using System.Diagnostics;
using System.Net;
using Capybara.Channels;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Contracts.Routines;
using Capybara.Gateway.Chat;
using Capybara.Gateway.Events;
using Capybara.Gateway.Settings;
using Capybara.Observability.Alerts;
using Capybara.Observability.Cost;
using Capybara.Policy.Safety;
using Capybara.Scheduling.Dispatch;
using Capybara.Scheduling.Execution;
using Capybara.Scheduling.History;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Triggers;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Gateway.Tests;

/// <summary>WP11 review round 1: dropped events are reported, one reply line per turn, settings and memory edge cases.</summary>
public sealed class ReviewFixTests
{
    private static readonly AgentId Ea = AgentId.Parse("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_still_queued_at_shutdown_are_counted_logged_and_alerted_after_a_bounded_drain()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var alerts = new InMemoryAlertSink();
        var registry = new RoutineRegistry();
        registry.AddOrUpdate(
            new RoutineSpec { Id = "on-push", Owner = Ea, Kind = TriggerKind.Event, EventFilter = "github:push repo=capybara", Prompt = "look" },
            RoutinePrincipal.Human("test"));
        var executor = new HangingExecutor();
        using var killSwitch = new KillSwitch(time);
        using var dispatcher = new RoutineDispatcher(executor, new RoutineRunHistory(alerts), alerts, new CostLedger(alerts, time), registry, killSwitch, time);
        using var worker = new EventIngressWorker(new EventRouter(registry, dispatcher, alerts, time), alerts, time, NullLogger<EventIngressWorker>.Instance);
        await worker.StartAsync(Ct);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(worker.TryEnqueue(new RoutineEventPayload("github", "push", """{"repo":"capybara"}""")));
        }

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var stopping = Stopwatch.StartNew();
        await worker.StopAsync(Ct);

        Assert.InRange(stopping.Elapsed, EventIngressWorker.DrainTimeout - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        var dropped = Assert.Single(alerts.Recent, a => a.Kind == EventIngressWorker.DroppedAlertKind);
        Assert.StartsWith("3 accepted webhook event(s)", dropped.Message, StringComparison.Ordinal);
        Assert.False(worker.TryEnqueue(new RoutineEventPayload("github", "push", "{}")));
    }

    [Fact]
    public async Task A_drained_queue_drops_nothing_and_raises_no_alert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var alerts = new InMemoryAlertSink();
        var registry = new RoutineRegistry();
        using var killSwitch = new KillSwitch(time);
        using var dispatcher = new RoutineDispatcher(new HangingExecutor(), new RoutineRunHistory(alerts), alerts, new CostLedger(alerts, time), registry, killSwitch, time);
        using var worker = new EventIngressWorker(new EventRouter(registry, dispatcher, alerts, time), alerts, time, NullLogger<EventIngressWorker>.Instance);
        await worker.StartAsync(Ct);
        Assert.True(worker.TryEnqueue(new RoutineEventPayload("github", "push", "{}")));

        await worker.StopAsync(Ct);

        Assert.DoesNotContain(alerts.Recent, a => a.Kind == EventIngressWorker.DroppedAlertKind);
    }

    [Fact]
    public void A_reply_to_joined_messages_is_one_line_found_under_every_id_and_survives_a_restart()
    {
        using var dir = new TempDirectory();
        var chat = new ChatHistory(dir.Path);
        var first = new FleetMessage { Id = "m1", To = Ea, Origin = MessageOrigin.User, Body = "first", CreatedAt = DateTimeOffset.UnixEpoch };
        var joined = new FleetMessage { Id = "m2", To = Ea, Origin = MessageOrigin.User, Body = "second", CreatedAt = DateTimeOffset.UnixEpoch };

        chat.RecordReply(Ea, first, [joined], "one answer", incognito: false, DateTimeOffset.UnixEpoch);

        foreach (var history in new[] { chat.Read(Ea, 100), new ChatHistory(dir.Path).Read(Ea, 100) })
        {
            var reply = Assert.Single(history, e => e.Role == "assistant");
            Assert.Equal("m1", reply.InReplyTo);
            Assert.Equal(["m2"], reply.AlsoInReplyTo!);
            Assert.Equal(["one answer"], Client.ChatReplies.To(history, "m2").Select(e => e.Content));
            Assert.Equal(2, history.Count(e => e.Role == "user"));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":null,"quietHours":{"enabled":true,"startsAt":"22:00:00","endsAt":"07:00:00"}}""")]
    [InlineData("""{"agents":{"ea":false},"quietHours":null}""")]
    [InlineData("null")]
    public void Saved_settings_missing_a_part_are_refused_as_invalid_data(string content)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, content);

        Assert.Throws<InvalidDataException>(() => NotificationSettings.ApplySaved(new ChannelOptions(), path));
    }

    [Fact]
    public async Task A_settings_file_missing_its_parts_does_not_stop_the_gateway_or_routing()
    {
        using var factory = new SettingsFileFactory("{}");
        _ = factory.CreateClient();
        var router = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<NotificationRouter>(factory.Services);

        await router.RouteAsync(NotificationFactory.Finished(Ea), Ct);

        Assert.NotNull(router.Options.QuietHours);
        Assert.Contains(factory.Channel.Sent, n => n.Kind == NotificationKind.Finished);
    }

    [Fact]
    public async Task Memory_reads_and_deletes_through_a_link_are_refused_as_bad_requests()
    {
        using var factory = new ApiFactory();
        _ = factory.CreateClient();
        var outside = Path.Combine(factory.StateRoot, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.md"), "not memory", Ct);
        var link = Path.Combine(factory.StateRoot, "memory", "agents", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryCreateDirectoryLink(link, outside))
        {
            Assert.Skip("Creating a directory link is not permitted here.");
        }

        var client = factory.CreateDeviceClient(factory.PhoneToken);
        var read = await client.GetAsync(new Uri("/api/memory/page?path=/agents/linked/secret.md", UriKind.Relative), Ct);
        var delete = await client.DeleteAsync(new Uri("/api/memory/page?path=/agents/linked/secret.md", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.True(File.Exists(Path.Combine(outside, "secret.md")));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // A junction needs no privilege on Windows, and .NET reports it as a link like a symlink.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null;
    }

    private sealed class SettingsFileFactory : ApiFactory
    {
        public SettingsFileFactory(string content)
        {
            Directory.CreateDirectory(Path.Combine(StateRoot, "channels"));
            File.WriteAllText(Path.Combine(StateRoot, "channels", "notification-settings.json"), content);
        }
    }

    private sealed class HangingExecutor : IRoutineExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoutineExecutionResult> ExecuteAsync(RoutineInvocation invocation, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
