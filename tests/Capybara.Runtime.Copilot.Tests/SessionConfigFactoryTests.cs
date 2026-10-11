using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;

namespace Capybara.Runtime.Copilot.Tests;

public sealed class SessionConfigFactoryTests
{
    private static readonly AgentDefinition Ea = TestAgents.Create("ea", a => a with
    {
        Soul = "You are calm.",
        OperatingRules = "Draft, never send.",
        Tools = [FleetTools.ListAgents, FleetTools.AskAgent, FleetTools.MemoryRead, FleetTools.Remember, "email_read"],
        Budget = new AgentBudget { SessionMaxCredits = 3m },
    });

    [Fact]
    public void Create_and_resume_register_the_same_handlers()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Ea);

        var create = f.Configs.CreateConfig(session);
        var resume = f.Configs.ResumeConfig(session);

        foreach (SessionConfigBase config in new SessionConfigBase[] { create, resume })
        {
            Assert.NotNull(config.OnPermissionRequest);
            Assert.NotNull(config.OnUserInputRequest);
            Assert.NotNull(config.Hooks?.OnPreToolUse);
            Assert.NotNull(config.OnEvent);
            Assert.Equal([.. Ea.Tools, .. SessionConfigFactory.InfrastructureTools], config.AvailableTools);
            Assert.False(config.EnableSessionStore);
            // Cumulative SDK window: never a limit on main, and an empty one so a resume clears any stored at creation;
            // the host enforces the 3-credit turn limit.
            Assert.Null(config.SessionLimits!.MaxAiCredits);
        }

        Assert.Equal(SessionIds.Main(Ea.Id), create.SessionId);
        Assert.True(create.InfiniteSessions?.Enabled);
    }

    [Fact]
    public void Only_allowlisted_custom_tools_are_registered()
    {
        using var f = new RuntimeFixture();
        var config = f.Configs.CreateConfig(f.Session(Ea));

        var names = config.Tools!.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([FleetTools.AskAgent, FleetTools.ListAgents, FleetTools.MemoryRead, FleetTools.Remember], names);
    }

    [Fact]
    public void Consult_sessions_lose_write_and_fan_out_tools_at_max_depth()
    {
        using var f = new RuntimeFixture();
        var config = f.Configs.CreateConfig(f.Session(Ea, SessionKind.Consult, depth: 2));

        Assert.DoesNotContain(FleetTools.AskAgent, config.AvailableTools!);
        Assert.DoesNotContain(FleetTools.Remember, config.AvailableTools!);
        Assert.Contains(FleetTools.MemoryRead, config.AvailableTools!);
        Assert.False(config.InfiniteSessions?.Enabled);
    }

    [Fact]
    public void System_message_customizes_identity_and_preserves_safety()
    {
        var message = SystemMessageBuilder.Build(Ea, SessionKind.Main);

        Assert.Equal(SystemMessageMode.Customize, message.Mode);
        Assert.Equal(SectionOverrideAction.Preserve, message.Sections![SystemMessageSection.Safety].Action);
        Assert.Equal("You are calm.", message.Sections[SystemMessageSection.Identity].Content);
        Assert.Contains("Draft, never send.", message.Sections[SystemMessageSection.CustomInstructions].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_runs_in_empty_mode_with_an_isolated_home()
    {
        using var f = new RuntimeFixture();
        var options = CopilotClientOptionsFactory.Create(Ea, f.Options);

        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.False(options.UseLoggedInUser);
        Assert.Equal(Path.Combine(f.Options.StateRoot, "agents", "ea", "copilot"), options.BaseDirectory);
    }

    [Fact]
    public void Self_test_flags_missing_and_unexpected_tools()
    {
        var result = AgentRuntime.Compare(["a", "b"], new HashSet<string>(StringComparer.Ordinal) { "b", "bash" });

        Assert.False(result.Passed);
        Assert.Equal(["a"], result.Missing);
        Assert.Equal(["bash"], result.Unexpected);
        Assert.True(AgentRuntime.Compare(["a"], new HashSet<string>(StringComparer.Ordinal) { "a" }).Passed);
    }
}
