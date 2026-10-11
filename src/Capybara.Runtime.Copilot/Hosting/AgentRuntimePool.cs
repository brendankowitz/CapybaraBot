using System.Collections.Concurrent;
using Capybara.Contracts.Agents;
using Capybara.Orchestration.Registry;
using Capybara.Runtime.Copilot.Supervision;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// All agent runtimes, one per agent registered when the pool was built. An agent added to the config later gets a
/// runtime on the next capybarad start; a removed agent's runtime is taken out with <see cref="RemoveAsync"/>.
/// </summary>
public sealed class AgentRuntimePool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IAgentRuntime> _runtimes;

    public AgentRuntimePool(AgentRegistry registry, Func<AgentDefinition, IAgentRuntime> factory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(factory);
        _runtimes = new(registry.All.ToDictionary(a => a.Id.Value, factory, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IAgentRuntime> All => [.. _runtimes.Values];

    /// <summary>Re-read on every enumeration, so the supervisor stops checking a runtime once it is removed.</summary>
    public IEnumerable<ISupervisedRuntime> Supervised
    {
        get
        {
            foreach (var runtime in _runtimes.Values)
            {
                if (runtime is ISupervisedRuntime supervised)
                {
                    yield return supervised;
                }
            }
        }
    }

    public IAgentRuntime Get(AgentId agent) =>
        Find(agent) ?? throw new KeyNotFoundException($"No runtime for agent '{agent}'.");

    public IAgentRuntime? Find(AgentId agent) => _runtimes.GetValueOrDefault(agent.Value);

    /// <summary>
    /// Takes <paramref name="agent"/>'s runtime out of the pool and disposes it, which ends its CLI process and releases
    /// its <c>BaseDirectory</c> (FR-AGT-07). Returns <see langword="false"/> when there was none. Idempotent.
    /// </summary>
    public async ValueTask<bool> RemoveAsync(AgentId agent)
    {
        if (!_runtimes.TryRemove(agent.Value, out var runtime))
        {
            return false;
        }

        await runtime.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync();
        }
    }
}
