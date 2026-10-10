using System.Collections.Concurrent;

namespace Fleet.Orchestration.Thread;

/// <summary>Bounded, user-visible log of all agent-to-agent traffic (FR-A2A-03).</summary>
public sealed class FleetThread(int capacity = 1_000)
{
    private readonly ConcurrentQueue<FleetThreadEntry> _entries = new();

    public event EventHandler<FleetThreadEntry>? Appended;

    public IReadOnlyList<FleetThreadEntry> Entries => [.. _entries];

    public void Append(FleetThreadEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > capacity && _entries.TryDequeue(out _))
        {
        }

        Appended?.Invoke(this, entry);
    }
}
