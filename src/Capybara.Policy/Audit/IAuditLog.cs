namespace Capybara.Policy.Audit;

public interface IAuditLog
{
    ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns <see langword="true"/> when every entry's hash and back-link are intact.</summary>
    ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a caller supplies; sequence and hashes are assigned by the log.</summary>
public sealed record AuditRecord(
    string Kind,
    string Decision,
    string? Agent = null,
    string? SessionId = null,
    string? Tool = null,
    string? ArgsHash = null,
    string? Detail = null,
    string? TraceId = null);
