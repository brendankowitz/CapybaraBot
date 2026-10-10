using System.Security.Cryptography;
using Capybara.Contracts.Agents;
using Capybara.Orchestration.Skills;
using Capybara.Policy.Rules;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using Capybara.Testing;
using GitHub.Copilot;
using RemoteSessionMode = GitHub.Copilot.Rpc.RemoteSessionMode;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-AGT-03/10, FR-SAF-01, FR-SKL-01/02/04, FR-CLI-08, FR-OBS-01: everything the SDK forgets on resume.</summary>
public sealed class SessionConfigCompletenessTests
{
    private const string ProviderKeyName = "byok/research";
    private const string ProviderKey = "sk-live-0123456789abcdefghijklmnop";
    private const string BrokerTokenName = "mcp/broker/research";
    private const string BrokerToken = "broker-token-0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PolicyRule[] Rules =
    [
        new() { Effect = RuleEffect.Deny, Tool = "shell", Argument = "command", ArgumentPattern = "rm*" },
        new() { Effect = RuleEffect.Ask, Agent = "research", Tool = "web_fetch" },
        new() { Effect = RuleEffect.Deny, Agent = "someone-else", Tool = "email_send" },
    ];

    [Fact]
    public async Task Create_and_resume_configs_match_on_every_re_registered_field()
    {
        using var f = new RuntimeFixture(Rules);
        var agent = await FullAgentAsync(f);
        var session = f.Session(agent);

        var create = await f.Configs.CreateConfigAsync(session, Ct);
        var resume = await f.Configs.ResumeConfigAsync(session, Ct);

        Assert.Equal(Snapshot(create), Snapshot(resume));
        foreach (SessionConfigBase config in new SessionConfigBase[] { create, resume })
        {
            Assert.NotNull(config.Hooks?.OnPreToolUse);
            Assert.NotNull(config.Hooks?.OnPostToolUse);
            Assert.NotNull(config.OnElicitationRequest);
            Assert.NotNull(config.OnUserInputRequest);
            Assert.NotNull(config.OnPermissionRequest);
        }

        var snapshot = Snapshot(create);
        Assert.Contains("deny=shell(rm*)", snapshot);
        Assert.Contains("ask=email_send,web_fetch", snapshot);
        Assert.Contains($"provider=openai|https://llm.example/v1|{ProviderKey}|gpt-x", snapshot);
        Assert.Contains(snapshot, l => l.StartsWith($"mcp:broker=http|ask|Bearer {BrokerToken}|", StringComparison.Ordinal));
        Assert.Contains(snapshot, l => l.StartsWith("mcp:local=stdio|search|", StringComparison.Ordinal) && l.EndsWith("|30000", StringComparison.Ordinal));
        Assert.Contains("toolsearch=True|40", snapshot);
        Assert.Contains("disabled=unpinned", snapshot);
    }

    [Fact]
    public async Task Provider_key_is_fetched_from_the_secret_store_on_every_build()
    {
        using var f = new RuntimeFixture();
        await f.Secrets.SetAsync(ProviderKeyName, ProviderKey, Ct);
        var session = f.Session(Researcher());

        var first = await f.Configs.CreateConfigAsync(session, Ct);
        Assert.Equal(1, f.Secrets.Reads);
        await f.Secrets.SetAsync(ProviderKeyName, ProviderKey + "-rotated", Ct);
        var second = await f.Configs.ResumeConfigAsync(session, Ct);

        Assert.Equal(2, f.Secrets.Reads);
        Assert.Equal(ProviderKey, first.Provider?.ApiKey);
        Assert.Equal(ProviderKey + "-rotated", second.Provider?.ApiKey);
        Assert.Equal("2024-10-21", first.Provider?.Azure?.ApiVersion);

        // A resolved key is registered with the redactor, so it can never come back through tool output.
        Assert.DoesNotContain(ProviderKey, f.Redactor.Redact($"the key is {ProviderKey}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_unreachable_secrets_fail_closed()
    {
        using var f = new RuntimeFixture();
        var session = f.Session(Researcher());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await f.Configs.CreateConfigAsync(session, Ct));

        // The synchronous builders cannot fetch secrets and must not silently drop the provider.
        await f.Secrets.SetAsync(ProviderKeyName, ProviderKey, Ct);
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(session));
        Assert.Throws<InvalidOperationException>(() => f.Configs.ResumeConfig(session));
    }

    [Fact]
    public void Unpinned_and_tampered_skills_are_disabled_and_tiers_are_ordered()
    {
        using var f = new RuntimeFixture();
        var shared = Path.Combine(f.Options.ConfigRoot, "skills");
        WriteSkill(shared, "pinned");
        WriteSkill(shared, "unpinned");
        WriteSkill(shared, "tampered");
        SkillLock.Pin(shared, "pinned");
        SkillLock.Pin(shared, "tampered");
        File.AppendAllText(Path.Combine(shared, "tampered", "SKILL.md"), "\nchanged after pinning");
        WriteSkill(f.Options.BuiltInSkillDirectory, "builtin");

        var config = f.Configs.CreateConfig(f.Session(TestAgents.Create("ea")));

        Assert.Equal([Path.Combine(f.Options.BuiltInSkillDirectory, "builtin"), Path.Combine(shared, "pinned")], config.SkillDirectories);
        Assert.Equal(["tampered", "unpinned"], config.DisabledSkills);
        Assert.True(config.EnableSkills);
    }

    [Fact]
    public void Only_verified_skill_folders_are_passed_never_tier_roots()
    {
        using var f = new RuntimeFixture();
        var tier = Path.Combine(f.Options.ConfigRoot, "agents", "ea", "skills");
        WriteSkill(tier, "good");
        WriteSkill(tier, "tampered");
        WriteSkill(tier, "bundle");
        WriteSkill(Path.Combine(tier, "bundle"), "inner");
        SkillLock.Pin(tier, "good");
        SkillLock.Pin(tier, "tampered");
        SkillLock.Pin(tier, "bundle");
        File.AppendAllText(Path.Combine(tier, "tampered", "SKILL.md"), "\nchanged after pinning");

        // Layouts the resolver never inspects but the runtime would load from a tier root.
        WriteSkill(Path.Combine(tier, "pending"), "good");
        WriteSkill(tier, "_foo");
        WriteSkill(Path.Combine(tier, "foo"), "bar");

        var config = f.Configs.CreateConfig(f.Session(TestAgents.Create("ea")));

        var good = Path.Combine(tier, "good");
        Assert.Equal([good], config.SkillDirectories);
        foreach (var root in config.SkillDirectories!)
        {
            Assert.Equal([Path.Combine(root, "SKILL.md")], Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories));
        }

        Assert.Contains("tampered", config.DisabledSkills!);
        Assert.Contains("bundle", config.DisabledSkills!);
    }

    [Fact]
    public async Task Remote_sessions_export_only_the_main_session_of_an_opted_in_agent()
    {
        using var f = new RuntimeFixture();
        var opted = TestAgents.Create("code", a => a with { EnableRemoteSessions = true });

        Assert.Equal(RemoteSessionMode.Export, (await f.Configs.CreateConfigAsync(f.Session(opted), Ct)).RemoteSession);
        Assert.Equal(RemoteSessionMode.Export, (await f.Configs.ResumeConfigAsync(f.Session(opted), Ct)).RemoteSession);
        Assert.Equal(RemoteSessionMode.Off, f.Configs.CreateConfig(f.Session(opted, SessionKind.Consult)).RemoteSession);
        Assert.Equal(RemoteSessionMode.Off, f.Configs.CreateConfig(f.Session(TestAgents.Create("ea"))).RemoteSession);
    }

    [Fact]
    public void Interpreter_mcp_servers_must_pin_their_script_too()
    {
        using var f = new RuntimeFixture();
        var node = WriteExecutable(f, "node");
        var script = WriteExecutable(f, "server.js");
        McpServerSpec Server(string? scriptPin, params string[] args) => new()
        {
            Name = "local",
            Type = "stdio",
            Command = node,
            Args = args,
            Tools = ["search"],
            Sha256 = Sha256(node),
            ScriptSha256 = scriptPin,
        };
        CapybaraSession SessionWith(McpServerSpec server) =>
            f.Session(TestAgents.Create("ea", a => a with { Tools = ["mcp:local-search"], McpServers = [server] }));

        var scriptPin = Sha256(script);
        Assert.Single(f.Configs.CreateConfig(SessionWith(Server(scriptPin, "--no-warnings", script))).McpServers!);

        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(Server(null, script))));
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(Server(scriptPin, "server.js"))));
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(Server(scriptPin, "-e", "require('x')"))));

        File.AppendAllText(script, "// trojan");
        Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(SessionWith(Server(scriptPin, script))));
    }

    [Fact]
    public void Stdio_mcp_servers_get_a_minimal_explicit_environment()
    {
        using var f = new RuntimeFixture();
        var exe = WriteExecutable(f, "server.bin");
        var agent = TestAgents.Create("ea", a => a with
        {
            Tools = ["mcp:local-search"],
            McpServers = [new McpServerSpec { Name = "local", Type = "stdio", Command = exe, Tools = ["search"], Sha256 = Sha256(exe) }],
        });

        var stdio = Assert.IsType<McpStdioServerConfig>(f.Configs.CreateConfig(f.Session(agent)).McpServers!["local"]);

        Assert.Equal(["HOME", "LANG", "PATH"], stdio.Env!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(f.Options.AgentWorkingDirectory("ea"), stdio.Env["HOME"]);
    }

    [Fact]
    public void Managed_settings_come_from_rules_and_approval_required_tools()
    {
        using var f = new RuntimeFixture(Rules);
        var agent = TestAgents.Create("research", a => a with { ApprovalRequiredTools = ["custom:email_send"] });

        var permissions = f.Configs.CreateConfig(f.Session(agent)).ManagedSettings?.Permissions;

        Assert.NotNull(permissions);
        Assert.Equal(["shell(rm*)"], permissions.Deny);
        Assert.Equal(["email_send", "web_fetch"], permissions.Ask);
    }

    [Fact]
    public void Tool_search_is_enabled_and_allowlisted()
    {
        using var f = new RuntimeFixture();
        var config = f.Configs.CreateConfig(f.Session(TestAgents.Create("ea")));

        Assert.True(config.ToolSearch?.Enabled);
        Assert.Contains("tool_search_tool", config.AvailableTools!);
        Assert.DoesNotContain("tool_search_tool", SessionConfigFactory.AvailableToolsFor(f.Session(TestAgents.Create("ea"))));
    }

    [Fact]
    public void Stdio_mcp_server_that_no_longer_matches_its_pin_is_refused()
    {
        using var f = new RuntimeFixture();
        var exe = WriteExecutable(f, "server.bin");
        var agent = TestAgents.Create("ea", a => a with
        {
            Tools = ["mcp:local-search"],
            McpServers = [new McpServerSpec { Name = "local", Type = "stdio", Command = exe, Tools = ["search"], Sha256 = Sha256(exe) }],
        });

        Assert.Single(f.Configs.CreateConfig(f.Session(agent)).McpServers!);

        File.AppendAllText(exe, "trojan");
        var ex = Assert.Throws<InvalidOperationException>(() => f.Configs.CreateConfig(f.Session(agent)));
        Assert.Contains("pinned SHA-256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void System_message_says_memory_is_not_authoritative()
    {
        var message = SystemMessageBuilder.Build(TestAgents.Create("ea"), SessionKind.Main);

        Assert.Contains("Memory is not authoritative", message.Sections![SystemMessageSection.CustomInstructions].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_honours_remote_sessions_and_exports_telemetry_without_content()
    {
        using var f = new RuntimeFixture();
        var remote = TestAgents.Create("code", a => a with { EnableRemoteSessions = true });

        Assert.True(CopilotClientOptionsFactory.Create(remote, f.Options).EnableRemoteSessions);
        Assert.False(CopilotClientOptionsFactory.Create(TestAgents.Create("ea"), f.Options).EnableRemoteSessions);
        Assert.Null(CopilotClientOptionsFactory.Create(remote, f.Options).Telemetry);

        f.Options.OtlpEndpoint = "http://localhost:4318";
        var telemetry = CopilotClientOptionsFactory.Create(remote, f.Options).Telemetry;

        Assert.NotNull(telemetry);
        Assert.Equal("http://localhost:4318", telemetry.OtlpEndpoint);
        Assert.Equal("otlp-http", telemetry.ExporterType);
        Assert.False(telemetry.CaptureContent);
        Assert.StartsWith("Capybara.", telemetry.SourceName, StringComparison.Ordinal);
    }

    private static AgentDefinition Researcher() => TestAgents.Create("research", a => a with
    {
        Provider = new ModelProviderSpec
        {
            Type = "azure",
            BaseUrl = "https://llm.example/v1",
            ApiKeySecret = ProviderKeyName,
            ModelId = "gpt-x",
            AzureApiVersion = "2024-10-21",
        },
    });

    private static async Task<AgentDefinition> FullAgentAsync(RuntimeFixture f)
    {
        await f.Secrets.SetAsync(ProviderKeyName, ProviderKey, Ct);
        await f.Secrets.SetAsync(BrokerTokenName, BrokerToken, Ct);
        var exe = WriteExecutable(f, "local-mcp.bin");

        var skills = Path.Combine(f.Options.ConfigRoot, "agents", "research", "skills");
        WriteSkill(skills, "pinned");
        WriteSkill(skills, "unpinned");
        SkillLock.Pin(skills, "pinned");
        f.Options.ToolSearchDeferThreshold = 40;

        return TestAgents.Create("research", a => a with
        {
            Tools = ["web_fetch", "mcp:broker-ask", "mcp:local-search"],
            ApprovalRequiredTools = ["email_send"],
            Provider = new ModelProviderSpec { Type = "openai", BaseUrl = "https://llm.example/v1", ApiKeySecret = ProviderKeyName, ModelId = "gpt-x" },
            McpServers =
            [
                new McpServerSpec { Name = "broker", Type = "http", Url = "http://127.0.0.1:7071/mcp/research", Tools = ["ask"], TokenSecret = BrokerTokenName },
                new McpServerSpec { Name = "local", Type = "stdio", Command = exe, Tools = ["search"], Sha256 = Sha256(exe), TimeoutSeconds = 30 },
            ],
        });
    }

    /// <summary>Flattens every field that must survive resume into comparable strings.</summary>
    private static List<string> Snapshot(SessionConfigBase c)
    {
        var permissions = c.ManagedSettings?.Permissions;
        List<string> lines =
        [
            $"model={c.Model}",
            $"available={string.Join(",", c.AvailableTools ?? [])}",
            $"tools={string.Join(",", (c.Tools ?? []).Select(t => t.Name).Order(StringComparer.Ordinal))}",
            $"deny={string.Join(",", permissions?.Deny ?? [])}",
            $"ask={string.Join(",", permissions?.Ask ?? [])}",
            $"provider={c.Provider?.Type}|{c.Provider?.BaseUrl}|{c.Provider?.ApiKey}|{c.Provider?.ModelId}",
            $"skilldirs={string.Join(",", c.SkillDirectories ?? [])}",
            $"disabled={string.Join(",", c.DisabledSkills ?? [])}",
            $"toolsearch={c.ToolSearch?.Enabled}|{c.ToolSearch?.DeferThreshold}",
            $"skills={c.EnableSkills}",
            $"store={c.EnableSessionStore}",
            $"limit={c.SessionLimits?.MaxAiCredits}",
            $"cwd={c.WorkingDirectory}",
        ];

        foreach (var (name, server) in (c.McpServers ?? new Dictionary<string, McpServerConfig>()).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            lines.Add(server switch
            {
                McpHttpServerConfig http => $"mcp:{name}=http|{string.Join(",", http.Tools ?? [])}|{http.Headers?["Authorization"]}|{http.Url}",
                McpStdioServerConfig stdio => $"mcp:{name}=stdio|{string.Join(",", stdio.Tools ?? [])}|{stdio.Command}|{stdio.Timeout}",
                _ => $"mcp:{name}=?",
            });
        }

        return lines;
    }

    private static void WriteSkill(string tier, string name)
    {
        var dir = Path.Combine(tier, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {name} skill\n---\nDo the {name} thing.\n");
    }

    private static string WriteExecutable(RuntimeFixture f, string name)
    {
        var path = Path.Combine(f.Root, name);
        File.WriteAllText(path, "vendored mcp server " + name);
        return path;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
