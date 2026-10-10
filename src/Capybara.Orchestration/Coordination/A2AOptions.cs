namespace Capybara.Orchestration.Coordination;

/// <summary>Concurrency and ping-pong limits (FR-A2A-11).</summary>
public sealed class A2AOptions
{
    public int MaxConcurrentNonMainSessions { get; set; } = 5;

    public int MaxConcurrentConsultsPerAgent { get; set; } = 2;

    public int MaxExchangesPerPairPerTask { get; set; } = 4;

    /// <summary>Consult answers are capped at about 2k tokens.</summary>
    public int MaxConsultAnswerChars { get; set; } = 8_000;

    public Contracts.Messaging.Budget DefaultConsultBudget { get; set; } = new(2m, 20, TimeSpan.FromSeconds(90));

    /// <summary>
    /// How long an idle per-pair exchange count is remembered (<see cref="ExchangeLedger"/>). Counts for delegated tasks
    /// are also kept in the task record.
    /// </summary>
    public TimeSpan ExchangeRetention { get; set; } = TimeSpan.FromHours(24);
}
