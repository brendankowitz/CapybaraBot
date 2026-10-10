using Capybara.Contracts.Agents;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Supervision;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>All agent runtimes, one per registered agent.</summary>
public sealed class AgentRuntimePool : IAsyncDisposable
{
    private readonly Dictionary<string, IAgentRuntime> _runtimes;

    public AgentRuntimePool(AgentRegistry registry, Func<AgentDefinition, IAgentRuntime> factory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(factory);
        _runtimes = registry.All.ToDictionary(a => a.Id.Value, factory, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IAgentRuntime> All => _runtimes.Values;

    public IEnumerable<ISupervisedRuntime> Supervised => _runtimes.Values.OfType<ISupervisedRuntime>();

    public IAgentRuntime Get(AgentId agent) =>
        Find(agent) ?? throw new KeyNotFoundException($"No runtime for agent '{agent}'.");

    public IAgentRuntime? Find(AgentId agent) => _runtimes.GetValueOrDefault(agent.Value);

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync();
        }
    }
}
