using System.Collections.Concurrent;
using Capybara.Contracts.Storage;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>In-memory <see cref="ISecretStore"/> that counts reads, so tests can prove secrets are fetched per build.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private int _reads;

    public int Reads => _reads;

    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);
    }

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        _values[name] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(name, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. _values.Keys.Order(StringComparer.Ordinal)]);
}
