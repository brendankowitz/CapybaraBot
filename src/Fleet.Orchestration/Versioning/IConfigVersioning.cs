namespace Fleet.Orchestration.Versioning;

/// <summary>Records every human-applied config change, so the config tree is git-versioned with a reviewable history.</summary>
public interface IConfigVersioning
{
    ValueTask CommitAsync(string message, CancellationToken cancellationToken = default);
}

public sealed class NoConfigVersioning : IConfigVersioning
{
    public ValueTask CommitAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
