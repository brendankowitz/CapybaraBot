using Capybara.Orchestration.Skills;

namespace Capybara.Orchestration.Tests;

public sealed class SkillScannerTests
{
    [Theory]
    [InlineData("allowed-tools: Bash, Read")]
    [InlineData("allowed-tools: [bash, read]")]
    [InlineData("allowedTools: [bash]")]
    public void Allowed_tools_is_flagged_as_granting_nothing(string declaration)
    {
        var skill = ConfigFixture.ValidSkill.ReplaceLineEndings("\n").Replace("description: Triage the inbox.", "description: Triage the inbox.\n" + declaration, StringComparison.Ordinal);

        var findings = SkillScanner.ScanSkill(skill);

        Assert.False(SkillScanner.Blocks(findings), string.Join("; ", findings.Select(f => f.Message)));
        Assert.Contains(findings, f => f.Severity == SkillFindingSeverity.Warning && f.Message.Contains("grants nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_skill_without_allowed_tools_has_no_warning_about_it()
    {
        Assert.DoesNotContain(SkillScanner.ScanSkill(ConfigFixture.ValidSkill), f => f.Message.Contains("grants nothing", StringComparison.Ordinal));
    }
}
