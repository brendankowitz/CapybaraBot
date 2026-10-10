namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// What one turn did, for credits and model attribution (FR-OBS-03) and run evidence (FR-OBS-06): spend per model,
/// every gated tool decision, the untrusted sources it read, and approvals it raised or (in a dry run) had auto-denied
/// (FR-RTN-08). Thread-safe: SDK callbacks arrive on their own threads.
/// </summary>
public sealed class SessionTurnLog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, decimal> _creditsByModel = new(StringComparer.Ordinal);
    private readonly List<string> _actions = [];
    private readonly SortedSet<string> _sources = new(StringComparer.Ordinal);
    private readonly List<string> _deniedApprovals = [];
    private readonly List<string> _pendingApprovals = [];

    public void RecordUsage(string model, decimal credits)
    {
        lock (_gate)
        {
            _creditsByModel[model] = _creditsByModel.GetValueOrDefault(model) + credits;
        }
    }

    public void RecordAction(string entry)
    {
        lock (_gate)
        {
            _actions.Add(entry);
        }
    }

    public void RecordSource(string source)
    {
        lock (_gate)
        {
            _sources.Add(source);
        }
    }

    /// <summary>A tool that needed a human and was auto-denied because the turn is a dry run.</summary>
    public void RecordDeniedApproval(string tool)
    {
        lock (_gate)
        {
            _deniedApprovals.Add(tool);
        }
    }

    /// <summary>An approval request the turn raised; the session must survive until it is decided.</summary>
    public void RecordPendingApproval(string approvalId)
    {
        lock (_gate)
        {
            _pendingApprovals.Add(approvalId);
        }
    }

    public TurnEvidence Snapshot()
    {
        lock (_gate)
        {
            var credits = _creditsByModel.Values.Sum();

            // The model that did most of the work is the run's model; every model is still in the ledger.
            var model = _creditsByModel.Count == 0 ? null : _creditsByModel.MaxBy(kv => kv.Value).Key;
            return new TurnEvidence(credits, model, [.. _actions], [.. _sources], [.. _deniedApprovals], [.. _pendingApprovals]);
        }
    }
}

/// <summary>Immutable snapshot of a <see cref="SessionTurnLog"/>.</summary>
public sealed record TurnEvidence(
    decimal Credits,
    string? Model,
    IReadOnlyList<string> ActionLog,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> DeniedApprovals,
    IReadOnlyList<string> PendingApprovals)
{
    public static TurnEvidence Empty { get; } = new(0m, null, [], [], [], []);
}
