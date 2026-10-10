using Fleet.Contracts.Storage;

namespace Fleet.Connections.Tests;

internal sealed class TestSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(_values.GetValueOrDefault(name));

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        _values[name] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _values.Remove(name);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([.. _values.Keys]);
}
