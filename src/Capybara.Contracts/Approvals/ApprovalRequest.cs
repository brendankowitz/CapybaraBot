using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Contracts.Approvals;

/// <summary>
/// A single-use, expiring human decision bound by a nonce to the canonical hash of one tool call's arguments (FR-SAF-04).
/// </summary>
public sealed record ApprovalRequest
{
    public required string Id { get; init; }

    /// <summary>128-bit nonce, hex encoded. Required to decide.</summary>
    public required string Nonce { get; init; }

    public required AgentId Agent { get; init; }

    public required string SessionId { get; init; }

    public required string Tool { get; init; }

    /// <summary>SHA-256 of the canonical JSON of the tool arguments.</summary>
    public required string ArgsHash { get; init; }

    public required string Preview { get; init; }

    public RiskLevel Risk { get; init; }

    public string? ReviewerVerdict { get; init; }

    public string? ReviewerRationale { get; init; }

    public MessageOrigin Origin { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public ApprovalStatus Status { get; init; } = ApprovalStatus.Pending;

    /// <summary>"Always allow" is never offered for high-risk tools.</summary>
    public bool OffersAlwaysAllow => Risk != RiskLevel.High;
}
