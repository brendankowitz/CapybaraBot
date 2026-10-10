using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Coordination;

/// <summary>
/// Outcome of an agent-to-agent call. <see cref="Content"/> is data from another agent — never instructions or consent
/// (FR-A2A-08).
/// </summary>
/// <remarks>
/// <c>ParentRemainder</c> is set once a call has spent a child budget (FR-A2A-05): the caller's envelope with that
/// budget carved out. The caller must continue with it, so sequential calls draw on what is left. It can be set on a
/// failed result too (a consult that started and then failed still spent its budget).
/// </remarks>
public sealed record A2AResult(
    bool Success,
    string? Error,
    string? Content = null,
    Taint Taint = Taint.Clean,
    string? Reference = null,
    Envelope? ParentRemainder = null)
{
    public static A2AResult Rejected(string error) => new(false, error);
}
