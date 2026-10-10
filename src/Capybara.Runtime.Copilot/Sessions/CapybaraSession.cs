using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Orchestration.Coordination;

namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// Everything the host knows about one session. Tool handlers and hooks close over this — never over model output —
/// so identity, depth, envelope, task and dry-run state are always host-assigned (FR-A2A-04).
/// </summary>
/// <remarks>Thread-safe: SDK callbacks and parallel tool calls touch it concurrently.</remarks>
public sealed class CapybaraSession
{
    private readonly Lock _gate = new();
    private Envelope _envelope;
    private SessionTurnLog _turn = new();
    private Task _envelopeTail = Task.CompletedTask;
    private CustomTools _customTools = CustomTools.None;

    public CapybaraSession(AgentDefinition agent, string sessionId, SessionKind kind, Envelope envelope, string? taskId = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(envelope);
        if (taskId is not null && kind != SessionKind.Task)
        {
            throw new ArgumentException("Only task sessions work on a delegated task.", nameof(taskId));
        }

        Agent = agent;
        SessionId = sessionId;
        Kind = kind;
        TaskId = taskId;
        Model = agent.Model;
        _envelope = envelope;
    }

    public AgentDefinition Agent { get; }

    public string SessionId { get; }

    public SessionKind Kind { get; }

    /// <summary>
    /// The delegated task this session works on (FR-A2A-04). Only such sessions get <c>complete_task</c> and
    /// <c>request_input</c>. <see langword="null"/> for an isolated routine run, which is a task session with no task.
    /// </summary>
    public string? TaskId { get; }

    /// <summary>The envelope of the turn currently running. Main sessions get a fresh root envelope per turn.</summary>
    public Envelope Envelope
    {
        get
        {
            lock (_gate)
            {
                return _envelope;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _envelope = value;
            }
        }
    }

    /// <summary>Who triggered the current turn. Only <see cref="MessageOrigin.User"/> can be consent.</summary>
    public MessageOrigin TurnOrigin { get; set; } = MessageOrigin.System;

    /// <summary>Routine that triggered the turn, for cost attribution. Its usage is reported by the routine run.</summary>
    public string? RoutineId { get; set; }

    /// <summary>
    /// The current turn is a routine dry run (FR-RTN-08): the gate auto-denies anything that would need a human, and
    /// <c>ask_user</c> reaches nobody.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>The model the session runs on (FR-AGT-08). Starts as the agent's model.</summary>
    public string Model { get; set; }

    /// <summary>
    /// The broker could not list this session's tools when its configuration was built, so they are missing. The
    /// runtime rebuilds the session before its next turn.
    /// </summary>
    public bool BrokerToolsUnavailable { get; set; }

    /// <summary>Record of the turn in progress.</summary>
    public SessionTurnLog Turn
    {
        get
        {
            lock (_gate)
            {
                return _turn;
            }
        }
    }

    /// <summary>A host-implemented tool registered on this session (fleet tools).</summary>
    public bool IsHostTool(string tool) => Volatile.Read(ref _customTools).Host.Contains(tool);

    /// <summary>A host-invoked tool wrapped by the policy gate (broker tools).</summary>
    public bool IsGatedTool(string tool) => Volatile.Read(ref _customTools).Gated.Contains(tool);

    /// <summary>Records the custom tools the session was last built with.</summary>
    public void SetCustomTools(IEnumerable<string> host, IEnumerable<string> gated) =>
        Volatile.Write(ref _customTools, new CustomTools(host.ToHashSet(StringComparer.Ordinal), gated.ToHashSet(StringComparer.Ordinal)));

    public bool IsReadOnly => Kind == SessionKind.Consult;

    public bool IsIncognito => Kind == SessionKind.Incognito;

    public int Depth => Envelope.Depth;

    private sealed record CustomTools(IReadOnlySet<string> Host, IReadOnlySet<string> Gated)
    {
        public static CustomTools None { get; } = new(new HashSet<string>(), new HashSet<string>());
    }

    /// <summary>Starts a fresh <see cref="Turn"/> record, optionally with a per-turn credit limit, and returns it.</summary>
    public SessionTurnLog BeginTurn(decimal? creditLimit = null)
    {
        lock (_gate)
        {
            _turn = new SessionTurnLog(creditLimit);
            return _turn;
        }
    }

    /// <summary>
    /// Runs one agent-to-agent call against the current envelope and continues with whatever budget it left
    /// (FR-A2A-05). Calls from one session are serialized, so parallel tool calls cannot each spend the same remainder.
    /// </summary>
    public async Task<A2AResult> SpendEnvelopeAsync(Func<Envelope, Task<A2AResult>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _envelopeTail;
            _envelopeTail = done.Task;
        }

        try
        {
            await previous;
            var result = await call(Envelope);
            if (result.ParentRemainder is { } remainder)
            {
                Envelope = remainder;
            }

            return result;
        }
        finally
        {
            done.SetResult();
        }
    }
}
