using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capybara.Connections.Model;
using Capybara.Contracts.Messaging;

namespace Capybara.Connections.Connectors;

/// <summary>
/// Base for REST connectors: resolves the operation, builds the provider request from named arguments, sends it with
/// the broker-held bearer token and caps the result size (FR-CON-01, FR-CON-10).
/// </summary>
public abstract partial class HttpConnector(HttpClient http, ConnectionDefinition connection) : IConnector
{
    /// <summary>Results longer than this are truncated before they reach model context.</summary>
    public const int MaxResultChars = 64 * 1024;

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ConnectionDefinition Connection { get; } = connection;

    public abstract IReadOnlyList<ConnectorOperation> Operations { get; }

    public async ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default)
    {
        var descriptor = Operations.FirstOrDefault(o => string.Equals(o.Name, operation, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return BrokerResult.Denied("unknown operation");
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.", nameof(args));
        }

        var spec = Build(operation, args);
        using var request = new HttpRequestMessage(spec.Method, spec.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (spec.Body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(spec.Body, Json), Encoding.UTF8, "application/json");
        }

        Configure(request);
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > MaxResultChars)
        {
            body = string.Concat(body.AsSpan(0, MaxResultChars), "\n[truncated]");
        }

        var text = $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}";
        var taint = descriptor.Untrusted ? Taint.Tainted : Taint.Clean;
        return new BrokerResult(response.IsSuccessStatusCode, text, taint, Encoding.UTF8.GetByteCount(text), response.IsSuccessStatusCode ? null : "connector returned failure");
    }

    /// <summary>Maps a known operation and its arguments to a provider request.</summary>
    protected abstract ConnectorRequest Build(string operation, JsonElement args);

    /// <summary>Adds provider-specific headers.</summary>
    protected virtual void Configure(HttpRequestMessage request)
    {
    }

    protected static string Required(JsonElement args, string name)
    {
        var value = Optional(args, name);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Missing argument: {name}") : value;
    }

    protected static string? Optional(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    protected static int Limit(JsonElement args, string name, int fallback, int max)
    {
        var raw = Optional(args, name);
        if (raw is null)
        {
            return fallback;
        }

        return int.TryParse(raw, out var parsed) && parsed > 0 ? Math.Min(parsed, max) : throw new ArgumentException($"{name} must be a positive integer.");
    }

    /// <summary>Header values (subjects, ids) must not smuggle extra headers.</summary>
    protected static string SingleLine(string value, string name) =>
        value.AsSpan().IndexOfAny('\r', '\n') >= 0 ? throw new ArgumentException($"{name} must be a single line.") : value;

    /// <summary>
    /// Recipients as a list of bare addresses. A single entry may not hide a second address (commas, angle brackets),
    /// otherwise recipient constraints could be bypassed (FR-CON-02).
    /// </summary>
    protected static IReadOnlyList<string> Addresses(JsonElement args, string name, bool required = true)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return required ? throw new ArgumentException($"Missing argument: {name}") : [];
        }

        string[] items = value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new ArgumentException($"{name} must contain only strings."))],
            JsonValueKind.String => [value.GetString()!],
            _ => throw new ArgumentException($"{name} must be a string or an array of strings."),
        };

        if ((required && items.Length == 0) || items.Any(a => !AddressPattern().IsMatch(a)))
        {
            throw new ArgumentException($"{name} must contain plain email addresses.");
        }

        return items;
    }

    protected static string Segment(string value, string name) =>
        value is "." or ".." || value.Contains('/', StringComparison.Ordinal)
            ? throw new ArgumentException($"{name} is not a valid identifier.")
            : Uri.EscapeDataString(value);

    [GeneratedRegex("^[^\\s@,;<>\"']+@[^\\s@,;<>\"']+$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}

/// <summary>A provider request built from validated arguments.</summary>
public sealed record ConnectorRequest(HttpMethod Method, string Uri, object? Body = null);
