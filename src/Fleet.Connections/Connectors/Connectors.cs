using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Contracts.Messaging;
using Fleet.Connections.Model;

namespace Fleet.Connections.Connectors;

/// <summary>Broker-side connector operation surface (FR-CON-03).</summary>
public interface IConnector
{
    string ConnectionId { get; }

    IReadOnlyList<string> Operations { get; }

    ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default);
}

public interface IConnectorRegistry
{
    IConnector? Find(string connectionId);

    IReadOnlyList<IConnector> List();
}

public sealed class InMemoryConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.Ordinal);

    public InMemoryConnectorRegistry Add(IConnector connector)
    {
        _connectors[connector.ConnectionId] = connector;
        return this;
    }

    public IConnector? Find(string connectionId) => _connectors.GetValueOrDefault(connectionId);

    public IReadOnlyList<IConnector> List() => [.. _connectors.Values];
}

public abstract class HttpConnector(HttpClient http, string connectionId) : IConnector
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ConnectionId { get; } = connectionId;

    public abstract IReadOnlyList<string> Operations { get; }

    public abstract ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default);

    protected async ValueTask<BrokerResult> SendAsync(HttpMethod method, string uri, string? body, string token, Taint taint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var concise = $"{(int)response.StatusCode} {response.ReasonPhrase}: {text}";
        return new BrokerResult(response.IsSuccessStatusCode, concise, taint, Encoding.UTF8.GetByteCount(concise), response.IsSuccessStatusCode ? null : "connector returned failure");
    }

    protected static string Required(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() ?? value.ToString() : throw new ArgumentException($"Missing argument: {name}");
}

/// <summary>Gmail REST connector; message bodies are tainted data (FR-SAF-03).</summary>
public sealed class GmailConnector(HttpClient http, string connectionId = "gmail") : HttpConnector(http, connectionId)
{
    public override IReadOnlyList<string> Operations { get; } = ["list_messages", "read_message", "create_draft", "send"];

    public override ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default) => operation switch
    {
        "list_messages" => SendAsync(HttpMethod.Get, $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q={Uri.EscapeDataString(args.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty)}", null, accessToken, Taint.Tainted, cancellationToken),
        "read_message" => SendAsync(HttpMethod.Get, $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(Required(args, "id"))}", null, accessToken, Taint.Tainted, cancellationToken),
        "create_draft" => SendAsync(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/drafts", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        "send" => SendAsync(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        _ => ValueTask.FromResult(new BrokerResult(false, "unknown operation", Taint.Clean, 0, "unknown operation")),
    };
}

/// <summary>Microsoft Graph mail connector; mail content is tainted data (FR-SAF-03).</summary>
public sealed class GraphMailConnector(HttpClient http, string connectionId = "graph-mail") : HttpConnector(http, connectionId)
{
    public override IReadOnlyList<string> Operations { get; } = ["list_messages", "read_message", "create_draft", "send"];

    public override ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default) => operation switch
    {
        "list_messages" => SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/messages", null, accessToken, Taint.Tainted, cancellationToken),
        "read_message" => SendAsync(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(Required(args, "id"))}", null, accessToken, Taint.Tainted, cancellationToken),
        "create_draft" => SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/messages", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        "send" => SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        _ => ValueTask.FromResult(new BrokerResult(false, "unknown operation", Taint.Clean, 0, "unknown operation")),
    };
}

/// <summary>Microsoft Graph calendar connector; invite bodies are tainted data (FR-SAF-03).</summary>
public sealed class GraphCalendarConnector(HttpClient http, string connectionId = "graph-calendar") : HttpConnector(http, connectionId)
{
    public override IReadOnlyList<string> Operations { get; } = ["list_events", "read_event", "create_event"];

    public override ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default) => operation switch
    {
        "list_events" => SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/events", null, accessToken, Taint.Tainted, cancellationToken),
        "read_event" => SendAsync(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/events/{Uri.EscapeDataString(Required(args, "id"))}", null, accessToken, Taint.Tainted, cancellationToken),
        "create_event" => SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/events", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        _ => ValueTask.FromResult(new BrokerResult(false, "unknown operation", Taint.Clean, 0, "unknown operation")),
    };
}

/// <summary>GitHub REST connector scoped by grants; push is intentionally unavailable (FR-CON-02).</summary>
public sealed class GitHubConnector(HttpClient http, string connectionId = "github") : HttpConnector(http, connectionId)
{
    public override IReadOnlyList<string> Operations { get; } = ["list_issues", "list_prs", "read_file", "create_branch", "create_pr"];

    public override ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default) => operation switch
    {
        "list_issues" => SendAsync(HttpMethod.Get, $"https://api.github.com/repos/{Required(args, "repo")}/issues", null, accessToken, Taint.Tainted, cancellationToken),
        "list_prs" => SendAsync(HttpMethod.Get, $"https://api.github.com/repos/{Required(args, "repo")}/pulls", null, accessToken, Taint.Tainted, cancellationToken),
        "read_file" => SendAsync(HttpMethod.Get, $"https://api.github.com/repos/{Required(args, "repo")}/contents/{Required(args, "path")}", null, accessToken, Taint.Tainted, cancellationToken),
        "create_branch" => SendAsync(HttpMethod.Post, $"https://api.github.com/repos/{Required(args, "repo")}/git/refs", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        "create_pr" => SendAsync(HttpMethod.Post, $"https://api.github.com/repos/{Required(args, "repo")}/pulls", args.GetRawText(), accessToken, Taint.Clean, cancellationToken),
        _ => ValueTask.FromResult(new BrokerResult(false, "unknown operation", Taint.Clean, 0, "unknown operation")),
    };
}
