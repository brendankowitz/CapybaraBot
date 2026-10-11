using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>Records formatted messages by level so tests can assert on degraded-mode warnings.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Critical { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Critical)
        {
            Critical.Add(formatter(state, exception));
        }
    }
}
