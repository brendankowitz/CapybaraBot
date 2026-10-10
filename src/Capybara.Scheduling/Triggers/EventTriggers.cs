using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capybara.Contracts.Messaging;
using Capybara.Scheduling.Execution;

namespace Capybara.Scheduling.Triggers;

/// <summary>Event source for webhook, mail/calendar push, GitHub and file triggers (FR-RTN-01, FR-RTN-07).</summary>
public interface IEventTriggerSource : IAsyncDisposable
{
    event EventHandler<RoutineEventPayload>? Fired;

    ValueTask StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Narrow event filter grammar: <c>source:event-type key=value</c> constraints (FR-RTN-07).</summary>
public sealed record EventFilter(string Source, string EventType, IReadOnlyDictionary<string, string> Constraints)
{
    public static EventFilter Parse(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        var parts = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = parts[0].Split(':', 2, StringSplitOptions.TrimEntries);
        if (header.Length != 2 || string.IsNullOrWhiteSpace(header[0]) || string.IsNullOrWhiteSpace(header[1]))
        {
            throw new FormatException("Event filter must start with source:event-type.");
        }

        var constraints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
            {
                throw new FormatException("Event filter constraints must be key=value.");
            }

            constraints[pair[0]] = pair[1];
        }

        var parsed = new EventFilter(header[0], header[1], constraints);
        if (!parsed.IsNarrow)
        {
            throw new FormatException("Event filters require source, event type and at least one constraint.");
        }

        return parsed;
    }

    public bool IsNarrow => !string.IsNullOrWhiteSpace(Source) && !string.IsNullOrWhiteSpace(EventType) && Constraints.Count > 0;

    public bool Matches(string source, string eventType, JsonElement payload)
    {
        if (!string.Equals(Source, source, StringComparison.Ordinal) || !string.Equals(EventType, eventType, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (path, expected) in Constraints)
        {
            if (!TryGetString(payload, path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, IReadOnlyList<string> path, out string? value)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
        return value is not null;
    }
}

/// <summary>Webhook event source helpers with HMAC verification and tainted payloads (FR-RTN-07).</summary>
public static class WebhookEventSource
{
    public const int MaxPayloadBytes = 256 * 1024;

    public static bool VerifyGitHubSignature(ReadOnlySpan<byte> body, string signatureHeader, ReadOnlySpan<byte> secret) =>
        VerifySignature(body, signatureHeader, secret, "sha256=");

    public static bool VerifySharedSecretSignature(ReadOnlySpan<byte> body, string signatureHeader, ReadOnlySpan<byte> secret) =>
        VerifySignature(body, signatureHeader, secret, "sha256=");

    public static RoutineEventPayload CreatePayload(string source, string eventType, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException("Event payload exceeds the configured size cap.");
        }

        return new RoutineEventPayload(source, eventType, Encoding.UTF8.GetString(body), Taint.Tainted);
    }

    private static bool VerifySignature(ReadOnlySpan<byte> body, string signatureHeader, ReadOnlySpan<byte> secret, string prefix)
    {
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        using var hmac = new HMACSHA256(secret.ToArray());
        if (!hmac.TryComputeHash(body, expected, out var written) || written != expected.Length)
        {
            return false;
        }

        byte[] actualBytes;
        try
        {
            actualBytes = Convert.FromHexString(signatureHeader[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return actualBytes.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actualBytes);
    }
}

/// <summary>Debounced FileSystemWatcher event source for file-change routines (FR-RTN-01).</summary>
public sealed class FileChangeEventSource : IEventTriggerSource
{
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private bool _started;

    public FileChangeEventSource(string directory, string filter = "*", TimeSpan? debounce = null)
    {
        _watcher = new FileSystemWatcher(directory, filter) { IncludeSubdirectories = false, EnableRaisingEvents = false };
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    public event EventHandler<RoutineEventPayload>? Fired;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _started = true;
        _watcher.EnableRaisingEvents = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_started)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_last.TryGetValue(e.FullPath, out var previous) && now - previous < _debounce)
        {
            return;
        }

        _last[e.FullPath] = now;
        var json = JsonSerializer.Serialize(new { path = e.FullPath, change = e.ChangeType.ToString() });
        Fired?.Invoke(this, new RoutineEventPayload("file", e.ChangeType.ToString(), json, Taint.Tainted));
    }
}
