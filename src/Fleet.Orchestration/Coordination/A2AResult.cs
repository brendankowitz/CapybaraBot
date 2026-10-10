using Fleet.Contracts.Messaging;

namespace Fleet.Orchestration.Coordination;

/// <summary>
/// Outcome of an agent-to-agent call. <see cref="Content"/> is data from another agent — never instructions or consent
/// (FR-A2A-08).
/// </summary>
public sealed record A2AResult(bool Success, string? Error, string? Content = null, Taint Taint = Taint.Clean, string? Reference = null)
{
    public static A2AResult Rejected(string error) => new(false, error);
}
