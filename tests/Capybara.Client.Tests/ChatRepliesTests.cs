using System.Net;
using System.Text;
using System.Text.Json;

namespace Capybara.Client.Tests;

public sealed class ChatRepliesTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    private static ChatEntryDto User(string content, string? id = null) => new(T, "user", content, MessageId: id);

    private static ChatEntryDto Assistant(string content, string? inReplyTo = null) => new(T, "assistant", content, InReplyTo: inReplyTo);

    [Fact]
    public void One_reply_to_several_joined_messages_is_found_under_each_id_and_shown_once()
    {
        ChatEntryDto[] history =
        [
            User("first", "m1"), User("second", "m2"),
            new(T, "assistant", "one answer", InReplyTo: "m1", AlsoInReplyTo: ["m2"]),
        ];

        Assert.Equal(["one answer"], ChatReplies.To(history, "m1").Select(e => e.Content));
        Assert.Equal(["one answer"], ChatReplies.To(history, "m2").Select(e => e.Content));
        Assert.Empty(ChatReplies.To(history, "m3"));

        // The id-less fallback still finds its anchor, although the deserialized list is a new instance every poll.
        var anchor = history[^1] with { AlsoInReplyTo = ["m2"] };
        Assert.Empty(ChatReplies.AfterSnapshot(history, anchor));
        Assert.Equal(["one answer"], ChatReplies.AfterSnapshot(history, history[0] with { }).Select(e => e.Content));
    }

    [Fact]
    public void A_repeated_prompt_gets_its_own_reply_not_the_previous_one()
    {
        ChatEntryDto[] history =
        [
            User("hello", "m1"), Assistant("old reply", "m1"),
            User("hello", "m2"), Assistant("new reply", "m2"),
        ];

        Assert.Equal(["new reply"], ChatReplies.To(history, "m2").Select(e => e.Content));
        Assert.Equal(["old reply"], ChatReplies.To(history, "m1").Select(e => e.Content));
    }

    [Fact]
    public void An_agent_initiated_message_in_between_is_never_taken_as_the_reply()
    {
        ChatEntryDto[] history =
        [
            User("do it", "m1"),
            Assistant("(unprompted) heads up: build failed"),
            Assistant("(someone else's) reply", "m0"),
            Assistant("the real reply", "m1"),
        ];

        Assert.Equal(["the real reply"], ChatReplies.To(history, "m1").Select(e => e.Content));
    }

    [Fact]
    public void A_reply_before_it_is_written_is_empty_even_if_other_assistant_entries_exist()
    {
        ChatEntryDto[] history = [User("do it", "m1"), Assistant("unprompted")];

        Assert.Empty(ChatReplies.To(history, "m1"));
    }

    [Fact]
    public void A_multi_part_reply_returns_every_part_in_order_and_ignores_the_prompt_text()
    {
        // The stored user text was normalized/joined by the server, so it never equals what was typed: ids make that irrelevant.
        ChatEntryDto[] history =
        [
            User("one\n\ntwo (joined by the inbox)", "m7"),
            Assistant("part 1", "m7"),
            Assistant("part 2", "m7"),
            Assistant("part 3", "m7"),
        ];

        Assert.Equal(["part 1", "part 2", "part 3"], ChatReplies.To(history, "m7").Select(e => e.Content));
    }

    [Fact]
    public void Fallback_considers_only_entries_after_the_snapshot_and_ignores_identical_earlier_prompts()
    {
        var before = Assistant("old reply");
        ChatEntryDto[] history =
        [
            User("hello"), before,
            User("hello"), Assistant("new reply 1"), Assistant("new reply 2"),
        ];

        Assert.Equal(["new reply 1", "new reply 2"], ChatReplies.AfterSnapshot(history, before).Select(e => e.Content));
        Assert.Empty(ChatReplies.AfterSnapshot(history[..2], before));
    }

    [Fact]
    public void Fallback_ignores_an_agent_message_that_arrived_before_our_message_and_a_normalized_prompt_still_matches()
    {
        var before = Assistant("old reply");
        ChatEntryDto[] history = [User("hello"), before, Assistant("(unprompted) news"), User("hello there (normalized by the server)"), Assistant("answer")];

        Assert.Equal(["answer"], ChatReplies.AfterSnapshot(history, before).Select(e => e.Content));
    }

    [Fact]
    public void Fallback_with_an_empty_snapshot_starts_from_the_beginning()
    {
        ChatEntryDto[] history = [User("hi"), Assistant("hello")];

        Assert.Equal(["hello"], ChatReplies.AfterSnapshot(history, null).Select(e => e.Content));
    }

    [Fact]
    public void Fallback_upgrades_to_ids_when_our_user_entry_has_one()
    {
        var before = Assistant("old");
        ChatEntryDto[] history = [before, User("x", "m9"), Assistant("agent noise"), Assistant("mine", "m9")];

        Assert.Equal(["mine"], ChatReplies.AfterSnapshot(history, before).Select(e => e.Content));
    }

    [Fact]
    public void Fallback_returns_nothing_when_the_snapshot_left_the_window()
    {
        ChatEntryDto[] history = [User("hi"), Assistant("hello")];

        Assert.Empty(ChatReplies.AfterSnapshot(history, Assistant("long gone")));
    }

    [Fact]
    public void With_a_message_id_the_snapshot_is_ignored()
    {
        var before = Assistant("old");
        ChatEntryDto[] history = [User("x", "m1"), Assistant("noise"), Assistant("mine", "m1"), before];

        Assert.Equal(["mine"], ChatReplies.Find(history, "m1", before).Select(e => e.Content));
        Assert.Empty(ChatReplies.Find(history, "other", before));
    }

    [Fact]
    public async Task Await_reply_polls_until_the_reply_to_the_message_id_appears()
    {
        var empty = Json([User("hi", "m1"), Assistant("unprompted")]);
        var done = Json([User("hi", "m1"), Assistant("unprompted"), Assistant("answer a", "m1"), Assistant("answer b", "m1")]);
        var handler = new SequenceHandler(empty, empty, done);
        var client = new CapybaraApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://capybara.example") });

        var replies = await client.AwaitReplyAsync("ea", "m1", null, TimeSpan.FromMilliseconds(1), 10, TestContext.Current.CancellationToken);

        Assert.Equal(["answer a", "answer b"], replies.Select(r => r.Content));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Await_reply_gives_up_with_an_empty_list()
    {
        var handler = new SequenceHandler(Json([User("hi", "m1")]));
        var client = new CapybaraApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://capybara.example") });

        var replies = await client.AwaitReplyAsync("ea", "m1", null, TimeSpan.FromMilliseconds(1), 3, TestContext.Current.CancellationToken);

        Assert.Empty(replies);
        Assert.Equal(3, handler.Calls);
    }

    private static string Json(ChatEntryDto[] entries) => JsonSerializer.Serialize(entries, JsonSerializerOptions.Web);

    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = bodies[Math.Min(Calls++, bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }
}
