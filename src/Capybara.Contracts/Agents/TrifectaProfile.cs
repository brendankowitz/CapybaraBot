namespace Capybara.Contracts.Agents;

/// <summary>
/// The three "lethal trifecta" legs an agent may hold (Rule of Two, FR-SAF-02).
/// </summary>
public sealed record TrifectaProfile
{
    public bool UntrustedInput { get; init; }

    public bool PrivateData { get; init; }

    public bool ExternalAction { get; init; }

    public int LegCount => (UntrustedInput ? 1 : 0) + (PrivateData ? 1 : 0) + (ExternalAction ? 1 : 0);

    public bool HoldsAllThree => LegCount == 3;
}
