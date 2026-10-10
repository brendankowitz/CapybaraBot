namespace Capybara.Scheduling.Lifecycle;

/// <summary>
/// Origin of a routine lifecycle request (FR-RTN-04: only the user creates, edits or enables routines).
/// <para>
/// There is no public constructor. <see cref="Human"/> must be called only by code that is handling an action the user
/// themselves took — an authenticated gateway request from a user surface, or the config reload that follows a human
/// edit or an approved proposal. It must never be called from agent-driven code paths (tool calls, event handlers,
/// routine executions); those use <see cref="Agent"/>, which every guarded operation rejects. This is a guard against
/// accidental misuse inside the host process, not a security boundary: authentication belongs to the gateway.
/// </para>
/// </summary>
public sealed class RoutinePrincipal
{
    private RoutinePrincipal(bool isHuman, string name)
    {
        IsHuman = isHuman;
        Name = name;
    }

    public bool IsHuman { get; }

    public string Name { get; }

    /// <summary>The user, acting through a user-originated surface. See the type remarks for when this may be called.</summary>
    public static RoutinePrincipal Human(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new RoutinePrincipal(isHuman: true, name);
    }

    /// <summary>An agent. Always rejected by operations that require a human.</summary>
    public static RoutinePrincipal Agent(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new RoutinePrincipal(isHuman: false, id);
    }

    internal void RequireHuman()
    {
        if (!IsHuman)
        {
            throw new InvalidOperationException("Only a human principal can change routine lifecycle state.");
        }
    }
}
