namespace Capybara.Scheduling.Validation;

/// <summary>Thrown when a routine breaks the FR-RTN-03 limits or is otherwise invalid; nothing is stored.</summary>
public sealed class RoutineValidationException(IReadOnlyList<string> errors) : InvalidOperationException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
