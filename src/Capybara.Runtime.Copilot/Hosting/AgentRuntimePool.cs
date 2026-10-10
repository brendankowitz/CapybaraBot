using Capybara.Contracts.Agents;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Supervision;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>All agent runtimes, one per registered agent.</summary>
public sealed class AgentRuntimePool : IAsyncDisposable
{
    private readonly Dictionary<string, AgentRuntime> _runtimes;

    public AgentRuntimePool(AgentRegistry registry, Func<AgentDefinition, AgentRuntime> factory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(factory);
        _runtimes = registry.All.ToDictionary(a => a.Id.Value, factory, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<AgentRuntime> All => _runtimes.Values;

    public IEnumerable<ISupervisedRuntime> Supervised => _runtimes.Values;

    public AgentRuntime Get(AgentId agent) =>
        _runtimes.TryGetValue(agent.Value, out var runtime)
            ? runtime
            : throw new KeyNotFoundException($"No runtime for agent '{agent}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync();
        }
    }
}
