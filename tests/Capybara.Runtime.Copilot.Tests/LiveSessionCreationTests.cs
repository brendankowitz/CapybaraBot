using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>
/// Live check against the Copilot CLI bundled with the SDK: every session kind the host builds is accepted by
/// <c>session.create</c> with budgets far below the CLI's 30-credit session-limit minimum (review round 2, N1).
/// Opt-in with <c>CAPYBARA_LIVE_CLI=1</c>. No token is needed: creation is validated locally.
/// </summary>
public sealed class LiveSessionCreationTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Budget = new AgentBudget { SessionMaxCredits = 5m },
        Tools = ["list_agents", "ask_agent", "delegate_task", "memory_read", "remember"],
    });

    [Fact]
    public async Task Every_session_kind_is_accepted_by_the_cli_with_small_budgets()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CAPYBARA_LIVE_CLI") == "1", "Set CAPYBARA_LIVE_CLI=1 to run against the bundled Copilot CLI.");
        var ct = TestContext.Current.CancellationToken;
        using var f = new RuntimeFixture(fleet: [Ea]);
        Directory.CreateDirectory(f.Options.AgentBaseDirectory(Ea.Id));
        Directory.CreateDirectory(f.Options.AgentWorkingDirectory(Ea.Id));
        var small = new Budget(1m, 10, TimeSpan.FromMinutes(1));

        var sessions = new List<CapybaraSession>
        {
            f.Session(Ea),
            f.Session(Ea, SessionKind.Task, taskId: new string('d', 32)),
            f.Session(Ea, SessionKind.Task),
            f.Session(Ea, SessionKind.Incognito),
            f.Session(Ea, SessionKind.Consult, depth: 1),
        };
        foreach (var session in sessions)
        {
            session.Envelope = session.Envelope with { Budget = small };
        }

        await using (var client = new CopilotClient(CopilotClientOptionsFactory.Create(Ea, f.Options)))
        {
            await client.StartAsync(ct);
            foreach (var session in sessions)
            {
                var config = await f.Configs.CreateConfigAsync(session, ct);
                await using var created = await client.CreateSessionAsync(config, ct);
                TestContext.Current.TestOutputHelper?.WriteLine($"{session.Kind}: created with MaxAiCredits={config.SessionLimits?.MaxAiCredits}");
                await client.DeleteSessionAsync(session.SessionId, ct);
            }
        }

        await using var reviewer = new CopilotClient(CopilotClientOptionsFactory.CreateForReviewer(f.Options));
        await reviewer.StartAsync(ct);
        var reviewerConfig = f.Configs.CreateReviewerConfig("reviewer_live", f.Options.ReviewerModel, Ea.Id);
        await using var review = await reviewer.CreateSessionAsync(reviewerConfig, ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"Reviewer: created with MaxAiCredits={reviewerConfig.SessionLimits?.MaxAiCredits}");
        await reviewer.DeleteSessionAsync("reviewer_live", ct);
    }
}
