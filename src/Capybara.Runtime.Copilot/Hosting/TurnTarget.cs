using Capybara.Contracts.Agents;
using Capybara.Contracts.Tasks;
using Capybara.Runtime.Copilot.Sessions;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// The session a turn runs in: the agent's main session, the session of a delegated task, an isolated routine run
/// (FR-RTN-01), or an incognito conversation (FR-MEM-12).
/// </summary>
public sealed record TurnTarget
{
    private TurnTarget(SessionKind kind, string? key, FleetTask? task)
    {
        Kind = kind;
        Key = key;
        Task = task;
    }

    public static TurnTarget Main { get; } = new(SessionKind.Main, null, null);

    public SessionKind Kind { get; }

    /// <summary>Task id, isolated-run key or conversation id; <see langword="null"/> for the main session.</summary>
    public string? Key { get; }

    /// <summary>The delegated task, whose envelope (and remaining budget) the turn runs under.</summary>
    public FleetTask? Task { get; }

    /// <summary>A turn in the session working on <paramref name="task"/> (FR-A2A-04).</summary>
    public static TurnTarget ForTask(FleetTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new TurnTarget(SessionKind.Task, task.Id, task);
    }

    /// <summary>A fresh, isolated task session that is not a delegated task, e.g. a cron routine run (FR-RTN-01).</summary>
    public static TurnTarget Isolated(string key) => new(SessionKind.Task, ValidKey(key), null);

    public static TurnTarget Incognito(string conversationId) => new(SessionKind.Incognito, ValidKey(conversationId), null);

    public string SessionIdFor(AgentId agent) => Kind switch
    {
        SessionKind.Main => SessionIds.Main(agent),
        SessionKind.Incognito => SessionIds.Incognito(agent, Key!),
        _ => SessionIds.Task(agent, Key!),
    };

    /// <summary>Keys become part of session ids, so they are limited to a safe alphabet.</summary>
    private static string ValidKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 80 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Session keys are 1-80 characters of letters, digits, '-' and '_'.", nameof(key));
        }

        return key;
    }
}
