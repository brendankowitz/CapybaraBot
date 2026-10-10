namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// Works out what happened to the turn that was running when a runtime crashed (FR-AGT-04). <c>session.idle</c> is
/// ephemeral, so after a crash the only record is the persisted event log.
/// </summary>
public interface ISessionReconciler
{
    Task<TurnRecovery> ReconcileAsync(ISupervisedRuntime runtime, CancellationToken cancellationToken);
}
