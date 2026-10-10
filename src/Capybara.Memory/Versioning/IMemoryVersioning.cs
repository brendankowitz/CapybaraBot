namespace Capybara.Memory.Versioning;

/// <summary>Records each memory change in version control (FR-MEM-01: git-versioned wiki).</summary>
public interface IMemoryVersioning
{
    ValueTask CommitAsync(string message, CancellationToken cancellationToken = default);
}

public sealed class NoMemoryVersioning : IMemoryVersioning
{
    public ValueTask CommitAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
