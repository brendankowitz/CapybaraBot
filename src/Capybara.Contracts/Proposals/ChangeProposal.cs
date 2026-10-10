using Capybara.Contracts.Agents;

namespace Capybara.Contracts.Proposals;

/// <summary>What a <c>propose_change</c> diff targets. Connections, grants and channels are never proposable (FR-CON-07).</summary>
public enum ProposalKind
{
    /// <summary>The agent's <c>SOUL.md</c>.</summary>
    Soul,

    /// <summary>The agent's <c>AGENTS.md</c> operating rules.</summary>
    OperatingRules,

    /// <summary>The agent's <c>agent.yaml</c> (grant changes are rejected).</summary>
    AgentConfig,

    /// <summary>A routine owned by the agent, as one YAML mapping.</summary>
    Routine,

    /// <summary>A skill's <c>SKILL.md</c>, staged under <c>skills/pending/</c> until approved (FR-SKL-03).</summary>
    Skill,
}

public enum ProposalStatus
{
    Pending,
    Applied,
    Rejected,

    /// <summary>The target changed after the proposal was made, so its diff no longer applies.</summary>
    Stale,

    /// <summary>Approved, but the resulting configuration failed validation; nothing was written.</summary>
    Invalid,
}

/// <summary>
/// An agent's request to change its own persona, config, routines or skills (FR-AGT-06, FR-SAF-11). It is only data:
/// a human reviews the diff out of band and applies it.
/// </summary>
public sealed record ChangeProposal
{
    public required string Id { get; init; }

    public required AgentId Agent { get; init; }

    public required ProposalKind Kind { get; init; }

    /// <summary>Routine id or skill name, when the kind needs one.</summary>
    public string? Name { get; init; }

    /// <summary>Path relative to the config root.</summary>
    public required string TargetPath { get; init; }

    public required string ProposedContent { get; init; }

    /// <summary>Unified diff against the content at <see cref="BaseHash"/>.</summary>
    public required string Diff { get; init; }

    /// <summary>SHA-256 of the target when proposed (empty when the target did not exist).</summary>
    public required string BaseHash { get; init; }

    public required string Rationale { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public ProposalStatus Status { get; init; } = ProposalStatus.Pending;

    public string? DecidedBy { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? Error { get; init; }
}
