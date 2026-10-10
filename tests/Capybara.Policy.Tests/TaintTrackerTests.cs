using Capybara.Contracts.Messaging;
using Capybara.Policy.Provenance;
using Capybara.Testing;

namespace Capybara.Policy.Tests;

public sealed class TaintTrackerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private string JournalPath => Path.Combine(_dir.Path, "policy", "taint.jsonl");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_tainted_session_is_still_tainted_after_a_restart()
    {
        var before = new TaintTracker(JournalPath);
        before.Apply("ea:main", Taint.Tainted);
        before.Propagate("ea:main", "ea:task:t1");
        before.Apply("ea:task:t2", Taint.Clean);

        var after = new TaintTracker(JournalPath);

        Assert.Equal(Taint.Tainted, after.Of("ea:main"));
        Assert.Equal(Taint.Tainted, after.Of("ea:task:t1"));
        Assert.Equal(Taint.Clean, after.Of("ea:task:t2"));
    }

    [Fact]
    public void A_forgotten_session_stays_forgotten_after_a_restart()
    {
        var before = new TaintTracker(JournalPath);
        before.MarkTainted("ea:consult:c1");
        before.MarkTainted("ea:main");
        before.Forget("ea:consult:c1");

        var after = new TaintTracker(JournalPath);

        Assert.False(after.IsTainted("ea:consult:c1"));
        Assert.True(after.IsTainted("ea:main"));
    }

    [Fact]
    public void Replay_compacts_the_journal_to_the_live_set()
    {
        var before = new TaintTracker(JournalPath);
        before.MarkTainted("a");
        before.MarkTainted("b");
        before.Forget("a");
        Assert.Equal(3, File.ReadAllLines(JournalPath).Length);

        _ = new TaintTracker(JournalPath);

        Assert.Single(File.ReadAllLines(JournalPath));
        Assert.True(new TaintTracker(JournalPath).IsTainted("b"));
    }

    [Fact]
    public void Marking_an_already_tainted_session_appends_nothing()
    {
        var tracker = new TaintTracker(JournalPath);
        tracker.MarkTainted("a");
        tracker.MarkTainted("a");
        tracker.Forget("never-tainted");

        Assert.Single(File.ReadAllLines(JournalPath));
    }

    [Fact]
    public void A_torn_last_line_from_a_crash_is_dropped()
    {
        new TaintTracker(JournalPath).MarkTainted("a");
        File.AppendAllText(JournalPath, "{\"sessionId\":\"b\",\"tain");

        var after = new TaintTracker(JournalPath);

        Assert.True(after.IsTainted("a"));
        Assert.False(after.IsTainted("b"));
        after.MarkTainted("c");
        Assert.True(new TaintTracker(JournalPath).IsTainted("c"));
    }
}
