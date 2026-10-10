using Capybara.Runtime.Copilot.Usage;
using Capybara.Testing;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

public sealed class UsageCaptureTests
{
    [Fact]
    public void Credits_come_from_nano_aiu_then_cost()
    {
        Assert.Equal(1.5m, UsageCapture.CreditsFrom(new AssistantUsageData { Model = "auto", CopilotUsage = new AssistantUsageCopilotUsage { TotalNanoAiu = 1_500_000_000 } }));
        Assert.Equal(0.25m, UsageCapture.CreditsFrom(new AssistantUsageData { Model = "auto", Cost = 0.25 }));
        Assert.Equal(0m, UsageCapture.CreditsFrom(new AssistantUsageData { Model = "auto" }));
    }

    [Fact]
    public void Usage_events_become_ledger_records_attributed_to_the_session()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(TestAgents.Create("ea"));
        session.RoutineId = "heartbeat";

        var record = f.Usage.ToRecord(session, new AssistantUsageEvent { Data = new AssistantUsageData { Model = "auto", Cost = 2 } });

        Assert.NotNull(record);
        Assert.Equal(2m, record.Credits);
        Assert.Equal("heartbeat", record.RoutineId);
        Assert.Equal(session.SessionId, record.SessionId);
    }

    [Fact]
    public async Task Routine_turn_usage_is_reported_by_the_run_not_written_twice_to_the_ledger()
    {
        using var f = new RuntimeFixture();
        var routine = f.Session(TestAgents.Create("ea"));
        routine.RoutineId = "heartbeat";
        var interactive = f.Session(TestAgents.Create("code"));
        var usage = new AssistantUsageEvent { Data = new AssistantUsageData { Model = "auto", Cost = 2 } };

        f.Usage.OnEvent(routine, usage);
        f.Usage.OnEvent(interactive, usage);

        Assert.Equal(2m, routine.Turn.Snapshot().Credits);
        Assert.Equal("auto", routine.Turn.Snapshot().Model);
        await Eventually.TrueAsync(() => f.Ledger.Records.Count == 1, "interactive usage recorded");
        Assert.Equal("code", Assert.Single(f.Ledger.Records).Agent.Value);
    }
}
