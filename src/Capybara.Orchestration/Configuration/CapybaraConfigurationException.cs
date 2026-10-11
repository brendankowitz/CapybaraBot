namespace Capybara.Orchestration.Configuration;

/// <summary>Raised when configuration is invalid. Startup fails closed.</summary>
public sealed class CapybaraConfigurationException : Exception
{
    public CapybaraConfigurationException()
    {
    }

    public CapybaraConfigurationException(string message)
        : base(message)
    {
    }

    public CapybaraConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CapybaraConfigurationException(IReadOnlyList<string> errors)
        : base("Invalid fleet configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; } = [];
}
