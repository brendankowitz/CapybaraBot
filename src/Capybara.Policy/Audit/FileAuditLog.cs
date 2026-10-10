using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Capybara.Policy.Audit;

/// <summary>
/// JSON-lines audit log. Each entry's hash covers its content plus the previous hash, so any edit or deletion breaks
/// the chain. Kept separate from telemetry.
/// </summary>
public sealed class FileAuditLog : IAuditLog, IDisposable
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _sequence = -1;
    private string _lastHash = GenesisHash;
    private bool _initialized;

    public FileAuditLog(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _time = time;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public async ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var entry = new AuditEntry
            {
                Sequence = _sequence + 1,
                At = _time.GetUtcNow(),
                Kind = record.Kind,
                Decision = record.Decision,
                Agent = record.Agent,
                SessionId = record.SessionId,
                Tool = record.Tool,
                ArgsHash = record.ArgsHash,
                Detail = record.Detail,
                TraceId = record.TraceId,
                PreviousHash = _lastHash,
            };
            entry = entry with { Hash = ComputeHash(entry) };

            var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
            await using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            _sequence = entry.Sequence;
            _lastHash = entry.Hash;
            return entry;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<AuditEntry> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            yield break;
        }

        await foreach (var line in File.ReadLinesAsync(_path, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions)
                    ?? throw new InvalidDataException("Corrupt audit entry.");
            }
        }
    }

    public async ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var previous = GenesisHash;
        long expected = 0;
        try
        {
            await foreach (var entry in ReadAllAsync(cancellationToken))
            {
                if (entry.Sequence != expected++ || entry.PreviousHash != previous || entry.Hash != ComputeHash(entry))
                {
                    return false;
                }

                previous = entry.Hash;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }

    public void Dispose() => _writeLock.Dispose();

    internal static string ComputeHash(AuditEntry entry)
    {
        var payload = JsonSerializer.Serialize(entry with { Hash = string.Empty }, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await foreach (var entry in ReadAllAsync(cancellationToken))
        {
            _sequence = entry.Sequence;
            _lastHash = entry.Hash;
        }

        _initialized = true;
    }
}
