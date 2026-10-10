namespace Fleet.Contracts.Routines;

/// <summary>Routine trigger kinds (FR-RTN-01).</summary>
public enum TriggerKind
{
    /// <summary>A periodic turn in the main session, limited to active hours.</summary>
    Heartbeat,

    /// <summary>A scheduled run in a fresh isolated task session.</summary>
    Cron,

    /// <summary>A webhook, mail/calendar push, GitHub event or file change. Payloads are always tainted.</summary>
    Event,
}
