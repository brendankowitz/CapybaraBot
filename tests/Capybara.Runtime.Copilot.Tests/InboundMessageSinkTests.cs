using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Runtime.Copilot.Inbox;
using Capybara.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-CLI-04 / NFR-02: channel text becomes a user DM to the front door, exactly once per channel message id.</summary>
public sealed class InboundMessageSinkTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InboundMessageSink Sink(RuntimeFixture f) =>
        new(f.Inbox, f.Registry, f.Options, f.Time, NullLogger<InboundMessageSink>.Instance);

    [Fact]
    public async Task Channel_text_becomes_a_user_dm_in_the_front_door_inbox()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        using var sink = Sink(f);

        await sink.ReceiveAsync("telegram", "42", "remind me at 5", "telegram:1001", Ct);

        var dm = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        Assert.Equal(MessageOrigin.User, dm.Origin);
        Assert.Equal("remind me at 5", dm.Body);
        Assert.Equal("telegram", dm.Channel);
        Assert.Null(dm.From);
    }

    [Fact]
    public async Task A_redelivery_is_dropped_even_after_the_dm_was_processed_and_the_host_restarted()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        using (var sink = Sink(f))
        {
            await sink.ReceiveAsync("telegram", "42", "hello", "telegram:7", Ct);
            await sink.ReceiveAsync("telegram", "42", "hello", "telegram:7", Ct);
        }

        var dm = Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
        await f.Inbox.AcknowledgeAsync(Ea.Id, dm.Id, Ct);

        using var restarted = Sink(f);
        await restarted.ReceiveAsync("telegram", "42", "hello", "telegram:7", Ct);

        Assert.Empty(await f.Inbox.ListAsync(Ea.Id, Ct));
    }

    [Fact]
    public async Task A_dm_enqueued_before_a_crash_lost_its_record_is_not_enqueued_twice()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        var id = InboundMessageSink.InboxIdFor(InboundMessageSink.KeyFor("telegram", "telegram:9"));
        await f.Inbox.EnqueueAsync(new FleetMessage { Id = id, To = Ea.Id, Origin = MessageOrigin.User, Body = "hello" }, Ct);
        using var sink = Sink(f);

        await sink.ReceiveAsync("telegram", "42", "hello", "telegram:9", Ct);

        Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
    }

    [Fact]
    public async Task The_same_id_on_another_channel_is_a_different_message()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        using var sink = Sink(f);

        await sink.ReceiveAsync("telegram", "42", "a", "1", Ct);
        await sink.ReceiveAsync("ntfy", "42", "b", "1", Ct);

        Assert.Equal(2, (await f.Inbox.ListAsync(Ea.Id, Ct)).Count);
    }

    [Fact]
    public async Task Records_expire_after_the_retention_window()
    {
        using var f = new RuntimeFixture(fleet: [Ea]);
        using var sink = Sink(f);
        await sink.ReceiveAsync("telegram", "42", "hello", "telegram:5", Ct);
        await f.Inbox.AcknowledgeAsync(Ea.Id, Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct)).Id, Ct);

        f.Time.Advance(f.Options.InboundDedupeRetention + TimeSpan.FromMinutes(1));
        await sink.ReceiveAsync("telegram", "42", "hello", "telegram:5", Ct);

        Assert.Single(await f.Inbox.ListAsync(Ea.Id, Ct));
    }

    [Fact]
    public async Task A_missing_front_door_throws_so_the_adapter_redelivers()
    {
        using var f = new RuntimeFixture(fleet: [TestAgents.Create("research")]);
        using var sink = Sink(f);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.ReceiveAsync("telegram", "42", "hello", "telegram:1", Ct));
    }
}
