namespace Capybara.Hosting.Agents;

/// <summary>Rootless Podman host that renders hardened Quadlet containers and executes through <c>podman exec</c> (FR-HST-01, FR-HST-03).</summary>
public sealed class PodmanHost(ICommandRunner runner, TimeProvider time) : IAgentHost
{
    private readonly ICommandRunner _runner = runner;
    private readonly TimeProvider _time = time;

    public async ValueTask ProvisionAsync(AgentHostSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["--user", "daemon-reload"]), cancellationToken);
    }

    public async ValueTask StartAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["--user", "start", $"capybara-sandbox@{agentId}.service"]), cancellationToken);
    }

    public async ValueTask StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        await _runner.RunAsync(new CommandRunRequest("systemctl", ["--user", "stop", $"capybara-sandbox@{agentId}.service"]), cancellationToken);
    }

    public async ValueTask<AgentHostHealth> HealthAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        var result = await _runner.RunAsync(new CommandRunRequest("podman", ["container", "inspect", "--format", "{{.State.Status}}", ContainerName(agentId)]), cancellationToken);
        var status = result.StandardOutput.Trim();
        return new AgentHostHealth(string.Equals(status, "running", StringComparison.Ordinal), status, _time.GetUtcNow());
    }

    public async ValueTask<ExecResult> ExecAsync(string agentId, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        var args = new List<string> { "exec", ContainerName(agentId) };
        args.AddRange(command);
        var result = await _runner.RunAsync(new CommandRunRequest("podman", args), cancellationToken);
        return new ExecResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    public ValueTask<SnapshotInfo> SnapshotAsync(string agentId, CancellationToken cancellationToken = default)
    {
        AgentIdValidator.Validate(agentId);
        return ValueTask.FromResult(new SnapshotInfo($"podman-{agentId}-{_time.GetUtcNow():yyyyMMddHHmmss}", $"volume:capybara-agent-{agentId}", _time.GetUtcNow()));
    }

    internal static string RenderQuadlet(AgentHostSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        AgentIdValidator.Validate(spec.AgentId);
        var memory = spec.MemoryMaxBytes is { } bytes ? UnitFileValue.PositiveBytes(bytes) : "1073741824";
        var image = UnitFileValue.Text(spec.Image, nameof(spec.Image), allowWhitespace: false);
        var workingDirectory = UnitFileValue.Text(spec.WorkingDirectory, nameof(spec.WorkingDirectory));
        var baseDirectory = UnitFileValue.Text(spec.BaseDirectory, nameof(spec.BaseDirectory), allowWhitespace: false);
        var env = string.Join(Environment.NewLine, spec.Environment.Select(kvp =>
            $"Environment={UnitFileValue.EnvironmentName(kvp.Key)}={UnitFileValue.Text(kvp.Value, kvp.Key, allowEmpty: true)}"));
        return $$"""
            [Unit]
            Description=Capybara sandbox for {{spec.AgentId}}

            [Container]
            ContainerName={{ContainerName(spec.AgentId)}}
            Image={{image}}
            ReadOnly=true
            NoNewPrivileges=true
            SecurityLabelDisable=false
            UserNS=keep-id
            PodmanArgs=--security-opt=no-new-privileges --security-opt=seccomp=/usr/share/containers/seccomp.json --cap-drop=all --network=none --memory={{memory}}
            Volume={{baseDirectory}}:/home/capybara/.copilot:Z
            WorkingDir={{workingDirectory}}
            {{env}}

            [Service]
            Restart=on-failure
            RestartSec=5s
            """;
    }

    private static string ContainerName(string agentId) => $"capybara-agent-{agentId}";
}
