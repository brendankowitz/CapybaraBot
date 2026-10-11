using Capybara.Contracts.Agents;

namespace Capybara.Observability.Cost;

/// <summary>One billed model call, captured from <c>assistant.usage</c> (FR-OBS-02, FR-OBS-03).</summary>
public sealed record UsageRecord(
    AgentId Agent,
    string Model,
    decimal Credits,
    DateTimeOffset At,
    string? SessionId = null,
    string? RoutineId = null,
    string? TaskId = null,
    long InputTokens = 0,
    long OutputTokens = 0);
