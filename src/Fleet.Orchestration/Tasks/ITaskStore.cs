using Fleet.Contracts.Tasks;

namespace Fleet.Orchestration.Tasks;

/// <summary>Durable task table for delegated work (FR-A2A-04).</summary>
public interface ITaskStore
{
    ValueTask SaveAsync(FleetTask task, CancellationToken cancellationToken = default);

    ValueTask<FleetTask?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<FleetTask> ListAsync(CancellationToken cancellationToken = default);
}
