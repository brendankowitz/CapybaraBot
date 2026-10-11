using Capybara.Connections.Client;
using Capybara.Memory.Access;
using Capybara.Memory.Admin;
using Capybara.Memory.Store;
using Capybara.Orchestration.Coordination;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Lifecycle;
using Capybara.Policy.Rules;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Scheduling.Lifecycle;

namespace Capybara.Gateway.Wiring;

// FR-AGT-07: what an archived or deleted agent leaves behind. AgentLifecycleService awaits these in registration order
// (see the composition root) before it deletes {state}/agents/{id}. Each is idempotent.

/// <summary>The broker drops the agent's grants and MCP bearer at once, not at its next restart.</summary>
public sealed class RevokeBrokerGrantsObserver(BrokerClient broker) : IAgentLifecycleObserver
{
    public async ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        await broker.RevokeAgentAsync(removal.Agent.Value, cancellationToken);
    }
}

/// <summary>
/// Every routine the agent owns leaves the scheduler now. The config reload removes the config-declared ones too; this
/// also covers routines that did not come from config, and does not wait for the reload loop.
/// </summary>
public sealed class UnscheduleRoutinesObserver(RoutineRegistry registry) : IAgentLifecycleObserver
{
    public ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);

        // Lifecycle operations are human-only (AgentLifecycleService enforces it), so this acts as that human.
        var principal = RoutinePrincipal.Human(removal.RemovedBy);
        var owned = registry.Routines.Where(r => r.Owner == removal.Agent).Select(r => r.Id);
        foreach (var id in owned.Concat(removal.Routines).Distinct(StringComparer.Ordinal).ToList())
        {
            registry.Remove(id, principal);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stops the agent's CLI and takes its runtime out of supervision, so its <c>BaseDirectory</c> is released before the
/// state directory is deleted. The agent is also paused, so its inbox pump only ever sees "blocked" from now on. A
/// deleted agent's journaled human pause is forgotten, so a later agent with the same id does not start paused.
/// </summary>
public sealed class StopRuntimeObserver(AgentRuntimePool runtimes, KillSwitch killSwitch, PausedAgentsJournal pausedAgents) : IAgentLifecycleObserver
{
    public async ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        killSwitch.Pause(removal.Agent);
        await runtimes.RemoveAsync(removal.Agent);
        if (removal.Kind == AgentRemovalKind.Deleted)
        {
            pausedAgents.Remove(removal.Agent);
        }
    }
}

/// <summary>
/// A deleted agent's queues go with it, so a later agent with the same id inherits nothing: the tasks it owns fail (their
/// requesters are told), the tasks it requested are cancelled, and its inbox is emptied. Archiving keeps them.
/// </summary>
public sealed class ClearAgentQueuesObserver(A2ACoordinator a2a, IInboxStore inbox) : IAgentLifecycleObserver
{
    public async ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        if (removal.Kind != AgentRemovalKind.Deleted)
        {
            return;
        }

        // Tasks first: failing one the agent requested of itself queues a notice to its own inbox, emptied next.
        await a2a.EndTasksOfAsync(removal.Agent, cancellationToken);
        foreach (var message in await inbox.ListAsync(removal.Agent, cancellationToken))
        {
            await inbox.AcknowledgeAsync(removal.Agent, message.Id, cancellationToken);
        }
    }
}

/// <summary>A deleted agent's private memory (<c>/agents/{id}/</c>, quarantined pages included) is removed with history.</summary>
/// <remarks>Archiving keeps memory, like the rest of the agent's state.</remarks>
public sealed class ClearAgentMemoryObserver(WikiStore store, MemoryAdmin admin) : IAgentLifecycleObserver
{
    public async ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        if (removal.Kind != AgentRemovalKind.Deleted)
        {
            return;
        }

        var prefix = $"/agents/{removal.Agent.Value}/";
        var context = new MemoryAccessContext { Agent = removal.Agent, SessionId = "human:lifecycle", CanReadUser = false };
        var pages = store.List(context, prefix)
            .Concat(store.Quarantined.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var page in pages)
        {
            await admin.DeletePageAsync(MemoryAdminPrincipal.Human, page, cancellationToken);
        }
    }
}

/// <summary>
/// A deleted agent's "Always allow" rules are forgotten, so a later agent with the same id does not inherit them.
/// Wildcard rules (<c>agent: "*"</c>) are the user's and stay.
/// </summary>
public sealed class RemoveLearnedRulesObserver(RulesEngine rules, LearnedRuleStore store) : IAgentLifecycleObserver
{
    public ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removal);
        if (removal.Kind == AgentRemovalKind.Deleted
            && rules.RemoveLearned(rule => string.Equals(rule.Agent, removal.Agent.Value, StringComparison.Ordinal)) > 0)
        {
            store.Save(rules.LearnedRules);
        }

        return ValueTask.CompletedTask;
    }
}
