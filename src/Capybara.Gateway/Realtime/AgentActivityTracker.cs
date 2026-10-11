using System.Collections.Concurrent;

namespace Capybara.Gateway.Realtime;

/// <summary>Tracks Working, Needs attention and Unread counts per agent for clients (FR-CLI-02).</summary>
/// <remarks>An agent can run several turns at once (main session, tasks, isolated routine runs); it is Working while any is.</remarks>
public sealed class AgentActivityTracker(ICapybaraEvents events)
{
    private readonly ConcurrentDictionary<string, AgentActivity> _agents = new(StringComparer.Ordinal);

    public AgentActivitySnapshot Get(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var activity = _agents.GetOrAdd(agentId, static _ => new AgentActivity());
        lock (activity)
        {
            return Snapshot(agentId, activity);
        }
    }

    public Task TurnStartedAsync(string agentId, CancellationToken cancellationToken = default) => UpdateAsync(agentId, static a => a.ActiveTurns++, cancellationToken);

    public Task TurnCompletedAsync(string agentId, bool needsAttention = false, CancellationToken cancellationToken = default) =>
        UpdateAsync(agentId, a =>
        {
            a.ActiveTurns = Math.Max(0, a.ActiveTurns - 1);
            a.NeedsAttention |= needsAttention;
            a.Unread++;
        }, cancellationToken);

    public Task ApprovalPendingAsync(string agentId, CancellationToken cancellationToken = default) =>
        UpdateAsync(agentId, a =>
        {
            a.NeedsAttention = true;
            a.Unread++;
        }, cancellationToken);

    public Task MarkReadAsync(string agentId, CancellationToken cancellationToken = default) =>
        UpdateAsync(agentId, a =>
        {
            a.Unread = 0;
            a.NeedsAttention = false;
        }, cancellationToken);

    private Task UpdateAsync(string agentId, Action<AgentActivity> update, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var activity = _agents.GetOrAdd(agentId, static _ => new AgentActivity());
        AgentActivitySnapshot snapshot;
        lock (activity)
        {
            update(activity);
            snapshot = Snapshot(agentId, activity);
        }

        return events.PublishAgentStatusChangedAsync(snapshot.AgentId, snapshot.State, snapshot.Unread, cancellationToken);
    }

    private static AgentActivitySnapshot Snapshot(string agentId, AgentActivity activity)
    {
        // Something waiting on the user outranks background work: it stays visible until read (FR-CLI-02).
        var state = activity.NeedsAttention
            ? AgentActivityState.NeedsAttention
            : activity.ActiveTurns > 0 ? AgentActivityState.Working : AgentActivityState.Idle;
        return new AgentActivitySnapshot(agentId, state, activity.Unread);
    }

    private sealed class AgentActivity
    {
        public int ActiveTurns { get; set; }

        public bool NeedsAttention { get; set; }

        public int Unread { get; set; }
    }
}

/// <summary>Current client-visible activity for one agent (FR-CLI-02).</summary>
public sealed record AgentActivitySnapshot(string AgentId, AgentActivityState State, int Unread);
