namespace Fleet.Orchestration.Configuration;

/// <summary>Raised when configuration is invalid. Startup fails closed.</summary>
public sealed class FleetConfigurationException : Exception
{
    public FleetConfigurationException()
    {
    }

    public FleetConfigurationException(string message)
        : base(message)
    {
    }

    public FleetConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FleetConfigurationException(IReadOnlyList<string> errors)
        : base("Invalid fleet configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; } = [];
}
