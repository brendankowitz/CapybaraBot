using System.Diagnostics;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Orchestration.Envelopes;

/// <summary>Result of trying to open a child hop.</summary>
public sealed record EnvelopeResult(Envelope? Child, Envelope? ParentRemainder, string? Error)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Creates and enforces A2A envelopes (FR-A2A-05): rejects depth &gt; 2, cycles in the hop path, exhausted budgets and
/// passed deadlines. A child's budget is carved out of the parent's remainder.
/// </summary>
public sealed class EnvelopeFactory(TimeProvider time)
{
    /// <param name="principal">The human the chain acts for.</param>
    /// <param name="rootAgent">The agent whose turn starts the chain.</param>
    /// <param name="budget">The chain's budget.</param>
    /// <param name="timeout">Time until the chain's deadline.</param>
    /// <param name="rootTaskId">An existing root task to continue, or a new one.</param>
    /// <param name="rootOrigin">Who started the chain; see <see cref="Envelope.RootOrigin"/>.</param>
    public Envelope CreateRoot(string principal, AgentId rootAgent, Budget budget, TimeSpan timeout, string? rootTaskId = null, MessageOrigin? rootOrigin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        var activity = Activity.Current;
        return new Envelope
        {
            TraceId = activity?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString(),
            TraceParent = activity?.Id,
            RootTaskId = rootTaskId ?? Guid.NewGuid().ToString("N"),
            Depth = 0,
            HopPath = [rootAgent],
            Budget = budget,
            Deadline = time.GetUtcNow() + timeout,
            OriginPrincipal = principal,
            Taint = Taint.Clean,
            RootOrigin = rootOrigin,
        };
    }

    /// <summary>
    /// The envelope of a turn that continues a chain (FR-A2A-05) — another agent's message, a notice about a task, or a
    /// resume after an approval: same trace, root task, parent task, depth, deadline, taint and principal, within the
    /// budget carved for the hop, never more than <paramref name="cap"/>. The hop path restarts at
    /// <paramref name="self"/> so the receiver can reply to the sender; the depth limit and the per-pair exchange limit
    /// (keyed by the root task) still bound the chain.
    /// </summary>
    /// <remarks>
    /// The deadline is a turn's time box. When the chain is <paramref name="self"/>'s own root that a person or the host
    /// started (depth 0) — a resume after the user decided an approval, or the result of a task it delegated — the turn
    /// gets a fresh one, <paramref name="cap"/>'s wall clock from now: the original turn's deadline says nothing about
    /// how long the user took to decide or the task took to run. Budget, depth and root origin are kept.
    /// </remarks>
    public Envelope ContinueFrom(Envelope incoming, AgentId self, Budget cap)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(cap);
        var budget = incoming.Budget;
        var ownRoot = incoming is { Depth: 0, RootOrigin: MessageOrigin.User or MessageOrigin.System, HopPath: [var root] } && root == self;
        return incoming with
        {
            HopPath = [self],
            Budget = new Budget(Math.Min(budget.Credits, cap.Credits), Math.Min(budget.ToolCalls, cap.ToolCalls), budget.Wallclock < cap.Wallclock ? budget.Wallclock : cap.Wallclock),
            Deadline = ownRoot ? time.GetUtcNow() + cap.Wallclock : incoming.Deadline,
        };
    }

    public EnvelopeResult CreateChild(Envelope parent, AgentId target, Budget requested, Taint callerTaint, string? parentTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(requested);

        if (parent.Depth + 1 > Envelope.MaxDepth)
        {
            return Fail($"depth limit reached ({Envelope.MaxDepth}).");
        }

        if (parent.HopPath.Contains(target))
        {
            return Fail($"cycle: {target} is already in the hop path {string.Join(" → ", parent.HopPath)}.");
        }

        if (time.GetUtcNow() >= parent.Deadline)
        {
            return Fail("deadline has passed.");
        }

        if (parent.Budget.IsExhausted || !parent.Budget.Fits(requested) || requested.IsExhausted)
        {
            return Fail("budget exhausted or request exceeds the parent's remainder.");
        }

        var child = parent with
        {
            ParentTaskId = parentTaskId ?? parent.ParentTaskId,
            Depth = parent.Depth + 1,
            HopPath = [.. parent.HopPath, target],
            Budget = requested,
            Taint = parent.Taint == Taint.Tainted || callerTaint == Taint.Tainted ? Taint.Tainted : Taint.Clean,
        };

        return new EnvelopeResult(child, parent with { Budget = parent.Budget.Subtract(requested) }, null);
    }

    private static EnvelopeResult Fail(string error) => new(null, null, error);
}
