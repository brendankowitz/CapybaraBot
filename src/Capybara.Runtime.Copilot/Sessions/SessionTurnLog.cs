namespace Capybara.Runtime.Copilot.Sessions;

/// <summary>
/// What one turn did, for credits and model attribution (FR-OBS-03) and run evidence (FR-OBS-06): spend per model,
/// every gated tool decision, the untrusted sources it read, and approvals it raised or (in a dry run) had auto-denied
/// (FR-RTN-08). Optionally enforces a credit limit for the turn: once spend passes it, the limit callbacks fire once so
/// the runtime can abort the run. Thread-safe: SDK callbacks arrive on their own threads.
/// </summary>
public sealed class SessionTurnLog(decimal? creditLimit = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, decimal> _creditsByModel = new(StringComparer.Ordinal);
    private readonly List<string> _actions = [];
    private readonly SortedSet<string> _sources = new(StringComparer.Ordinal);
    private readonly List<string> _deniedApprovals = [];
    private readonly List<string> _pendingApprovals = [];
    private readonly List<Action> _onLimitExceeded = [];
    private decimal _credits;
    private bool _limitExceeded;

    /// <summary>The most this turn may spend, or <see langword="null"/> for no per-turn limit.</summary>
    public decimal? CreditLimit { get; } = creditLimit;

    public bool LimitExceeded
    {
        get
        {
            lock (_gate)
            {
                return _limitExceeded;
            }
        }
    }

    public void RecordUsage(string model, decimal credits)
    {
        Action[] fire = [];
        lock (_gate)
        {
            _creditsByModel[model] = _creditsByModel.GetValueOrDefault(model) + credits;
            _credits += credits;
            if (!_limitExceeded && CreditLimit is { } limit && _credits > limit)
            {
                _limitExceeded = true;
                fire = [.. _onLimitExceeded];
                _onLimitExceeded.Clear();
            }
        }

        foreach (var callback in fire)
        {
            callback();
        }
    }

    /// <summary>
    /// Runs <paramref name="callback"/> once when the turn passes its credit limit — immediately if it already has.
    /// Dispose the result to unsubscribe.
    /// </summary>
    public IDisposable OnLimitExceeded(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_limitExceeded)
            {
                _onLimitExceeded.Add(callback);
                return new Subscription(this, callback);
            }
        }

        callback();
        return new Subscription(this, callback);
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

    /// <summary>A tool that needed a human, or acts outside a test, and was auto-denied because the turn is a dry run.</summary>
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
            // The model that did most of the work is the run's model; every model is still in the ledger.
            var model = _creditsByModel.Count == 0 ? null : _creditsByModel.MaxBy(kv => kv.Value).Key;
            return new TurnEvidence(_credits, model, [.. _actions], [.. _sources], [.. _deniedApprovals], [.. _pendingApprovals]);
        }
    }

    private void Unsubscribe(Action callback)
    {
        lock (_gate)
        {
            _onLimitExceeded.Remove(callback);
        }
    }

    private sealed class Subscription(SessionTurnLog log, Action callback) : IDisposable
    {
        public void Dispose() => log.Unsubscribe(callback);
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
