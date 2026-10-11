namespace Capybara.Contracts.Messaging;

/// <summary>Remaining resources for a chain of agent-to-agent calls.</summary>
public sealed record Budget(decimal Credits, int ToolCalls, TimeSpan Wallclock)
{
    public bool IsExhausted => Credits <= 0 || ToolCalls <= 0 || Wallclock <= TimeSpan.Zero;

    public bool Fits(Budget requested) =>
        requested.Credits <= Credits && requested.ToolCalls <= ToolCalls && requested.Wallclock <= Wallclock;

    public Budget Subtract(Budget other) =>
        new(Credits - other.Credits, ToolCalls - other.ToolCalls, Wallclock - other.Wallclock);
}
