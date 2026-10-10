namespace Capybara.Hosting.Agents;

/// <summary>systemd-backed local host for per-agent services with resource and network hardening (FR-HST-01, FR-HST-02).</summary>
public sealed class SystemdHost(ICommandRunner runner, TimeProvider time) : IAgentHost
{
    private readonly ICommandRunner _runner = runner;
    private readonly TimeProvider _time = time;

    public async ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["daemon-reload"]), cancellationToken);
    }

    public async ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["start", UnitName(agentId)]), cancellationToken);
    }

    public async ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["stop", UnitName(agentId)]), cancellationToken);
    }

    public async ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        var result = await _runner.RunAsync(new CommandRunRequest("systemctl", ["is-active", UnitName(agentId)]), cancellationToken);
        var status = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError.Trim() : result.StandardOutput.Trim();
        return new AgentHostHealth(result.ExitCode == 0, status, _time.GetUtcNow());
    }

    public async ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        var args = new List<string> { "--unit", $"capybara-agent-exec-{agentId}", "--wait", "--pipe", "--same-dir", "--property", $"JoinsNamespaceOf={UnitName(agentId)}", "--" };
        args.AddRange(command);
        var result = await _runner.RunAsync(new CommandRunRequest("systemd-run", args), cancellationToken);
        return new ExecResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    public ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        return ValueTask.FromResult(new SnapshotInfo($"systemd-{agentId}-{_time.GetUtcNow():yyyyMMddHHmmss}", $"/var/lib/capybara/agents/{agentId}", _time.GetUtcNow()));
    }

    internal static string RenderDropIn(AgentHostSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        var memory = spec.MemoryMaxBytes is { } bytes ? UnitFileValue.PositiveBytes(bytes) : "1G";
        var cpu = string.IsNullOrWhiteSpace(spec.CpuQuota) ? "100%" : UnitFileValue.CpuQuota(spec.CpuQuota);
        var workingDirectory = UnitFileValue.Text(spec.WorkingDirectory, nameof(spec.WorkingDirectory), allowWhitespace: false);
        var baseDirectory = UnitFileValue.Text(spec.BaseDirectory, nameof(spec.BaseDirectory), allowWhitespace: false);
        // systemd IPAddressAllow takes addresses/CIDRs only; domain allowlists (and "*") are enforced by the srt proxy
        // (FR-SAF-07), not here. Each line is re-rendered from the parsed address, never copied from the input.
        var allows = string.Join(Environment.NewLine, spec.EgressDomains.Select(UnitFileValue.IpOrCidr).OfType<string>().Select(a => $"IPAddressAllow={a}"));
        return $$"""
            [Service]
            Environment=CAPYBARA_AGENT_ID={{spec.AgentId}}
            WorkingDirectory={{workingDirectory}}
            ReadWritePaths={{baseDirectory}} {{workingDirectory}}
            MemoryMax={{memory}}
            CPUQuota={{cpu}}
            IPAddressDeny=any
            IPAddressAllow=localhost
            {{allows}}
            NoNewPrivileges=yes
            ProtectSystem=strict
            ProtectHome=yes
            PrivateTmp=yes
            PrivateDevices=yes
            RestrictSUIDSGID=yes
            LockPersonality=yes
            """;
    }

    private static string UnitName(string agentId) => $"capybara-agent@{agentId}.service";
}
