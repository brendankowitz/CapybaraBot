namespace Fleet.Contracts.Messaging;

/// <summary>Provenance label for content that originated from untrusted input (FR-SAF-03).</summary>
public enum Taint
{
    Clean,
    Tainted,
}
