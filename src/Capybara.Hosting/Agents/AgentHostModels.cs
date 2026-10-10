namespace Capybara.Hosting.Agents;

/// <summary>Host kinds selected from configuration so agents can move without code forks (NFR-09).</summary>
public enum AgentHostKind
{
    Systemd,
    Podman,
    Aca,
    Hybrid,
}

/// <summary>Runtime resource and sandbox settings for a hosted agent (FR-HST-01, FR-HST-03).</summary>
public sealed record AgentHostSpec(
    string AgentId,
    string BaseDirectory,
    string WorkingDirectory,
    IReadOnlyList<string> EgressDomains,
    long? MemoryMaxBytes,
    string? CpuQuota,
    string Image,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>Observed runtime health for an agent host (FR-HST-01).</summary>
public sealed record AgentHostHealth(bool Running, string Status, DateTimeOffset CheckedAt);

/// <summary>Result of an in-sandbox command execution (FR-HST-01).</summary>
public sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Metadata about a runtime snapshot for backup and migration (FR-HST-01, FR-HST-05).</summary>
public sealed record SnapshotInfo(string SnapshotId, string Path, DateTimeOffset CreatedAt);
