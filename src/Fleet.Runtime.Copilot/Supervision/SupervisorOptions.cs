namespace Fleet.Runtime.Copilot.Supervision;

public sealed class SupervisorOptions
{
    public const string SectionName = "Fleet:Supervisor";

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A runtime that crashes this many times within <see cref="CrashWindow"/> is quarantined.</summary>
    public int MaxCrashes { get; set; } = 5;

    public TimeSpan CrashWindow { get; set; } = TimeSpan.FromMinutes(30);
}
