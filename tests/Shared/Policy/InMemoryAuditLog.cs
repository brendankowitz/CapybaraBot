using System.Runtime.CompilerServices;
using Fleet.Policy.Audit;

namespace Fleet.Testing;

/// <summary>Records audit entries in memory; no hash chain.</summary>
internal sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var entry = new AuditEntry
            {
                Sequence = _entries.Count + 1,
                At = DateTimeOffset.UnixEpoch,
                Kind = record.Kind,
                Decision = record.Decision,
                Agent = record.Agent,
                SessionId = record.SessionId,
                Tool = record.Tool,
                ArgsHash = record.ArgsHash,
                Detail = record.Detail,
                TraceId = record.TraceId,
                PreviousHash = string.Empty,
            };
            _entries.Add(entry);
            return ValueTask.FromResult(entry);
        }
    }

    public async IAsyncEnumerable<AuditEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var entry in Entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}
