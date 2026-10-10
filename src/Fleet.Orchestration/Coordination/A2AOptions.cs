namespace Fleet.Orchestration.Coordination;

/// <summary>Concurrency and ping-pong limits (FR-A2A-11).</summary>
public sealed class A2AOptions
{
    public int MaxConcurrentNonMainSessions { get; set; } = 5;

    public int MaxConcurrentConsultsPerAgent { get; set; } = 2;

    public int MaxExchangesPerPairPerTask { get; set; } = 4;

    /// <summary>Consult answers are capped at about 2k tokens.</summary>
    public int MaxConsultAnswerChars { get; set; } = 8_000;

    public Contracts.Messaging.Budget DefaultConsultBudget { get; set; } = new(2m, 20, TimeSpan.FromSeconds(90));
}
