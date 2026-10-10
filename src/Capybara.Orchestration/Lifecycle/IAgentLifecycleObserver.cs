namespace Capybara.Orchestration.Lifecycle;

/// <summary>
/// Undoes an agent's live footprint when it is archived or deleted (FR-AGT-07): the broker revokes its grants, the
/// scheduler unschedules its routines, the runtime host stops its CLI process. Register any number in DI;
/// <see cref="AgentLifecycleService"/> awaits each in turn.
/// </summary>
/// <remarks>
/// Called after the agent is gone from the live config (the registry has already raised <c>Changed</c>) and, for a
/// delete, before <c>{state}/agents/{id}</c> is removed, so a runtime must have released its <c>BaseDirectory</c> when
/// this returns. Must be idempotent. A failure does not stop the other observers; the lifecycle call reports it.
/// </remarks>
public interface IAgentLifecycleObserver
{
    ValueTask OnAgentRemovedAsync(AgentRemoval removal, CancellationToken cancellationToken);
}
