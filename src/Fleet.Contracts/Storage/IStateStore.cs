namespace Fleet.Contracts.Storage;

/// <summary>
/// Durable key/value state partitioned by collection (FR-HST-06): SQLite now, Azure SQL or Postgres later.
/// Values are opaque JSON documents; callers own serialization.
/// </summary>
public interface IStateStore
{
    ValueTask<string?> GetAsync(string collection, string key, CancellationToken cancellationToken = default);

    ValueTask PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default);

    /// <summary>Returns <see langword="true"/> when the key existed.</summary>
    ValueTask<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<KeyValuePair<string, string>>> ListAsync(string collection, string? keyPrefix = null, CancellationToken cancellationToken = default);
}
