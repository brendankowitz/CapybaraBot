using System.Globalization;
using System.Text.Json;
using Capybara.Connections.Model;

namespace Capybara.Connections.Connectors;

/// <summary>Microsoft Graph mail connector (FR-CON-01). Mail content is third-party data (FR-SAF-03).</summary>
public sealed class GraphMailConnector(HttpClient http, ConnectionDefinition connection) : HttpConnector(http, connection)
{
    private const string Base = "https://graph.microsoft.com/v1.0/me";

    public override IReadOnlyList<ConnectorOperation> Operations { get; } = MailOperations.All;

    protected override ConnectorRequest Build(string operation, JsonElement args) => operation switch
    {
        MailOperations.List => new(HttpMethod.Get, $"{Base}/messages?$top={Limit(args, "max", 20, 100)}&$select=id,subject,from,receivedDateTime,bodyPreview{Search(args)}"),
        MailOperations.Read => new(HttpMethod.Get, $"{Base}/messages/{Segment(Required(args, "id"), "id")}"),
        MailOperations.Draft => new(HttpMethod.Post, $"{Base}/messages", Message(args)),
        MailOperations.Send => new(HttpMethod.Post, $"{Base}/sendMail", new { message = Message(args), saveToSentItems = true }),
        _ => throw new ArgumentException($"Unknown operation: {operation}"),
    };

    private static string Search(JsonElement args) =>
        Optional(args, "query") is { Length: > 0 } query ? $"&$search={Uri.EscapeDataString($"\"{query.Replace("\"", string.Empty, StringComparison.Ordinal)}\"")}" : string.Empty;

    private static object Message(JsonElement args) => new
    {
        subject = SingleLine(Required(args, "subject"), "subject"),
        body = new { contentType = "Text", content = Required(args, "body") },
        toRecipients = Addresses(args, "to").Select(a => new { emailAddress = new { address = a } }).ToArray(),
    };
}

/// <summary>Microsoft Graph calendar connector (FR-CON-01). Invite content is third-party data (FR-SAF-03).</summary>
public sealed class GraphCalendarConnector(HttpClient http, ConnectionDefinition connection) : HttpConnector(http, connection)
{
    public override IReadOnlyList<ConnectorOperation> Operations { get; } = CalendarOperations.All;

    protected override ConnectorRequest Build(string operation, JsonElement args)
    {
        var calendar = Optional(args, "calendarId") is { } id && id != "primary"
            ? $"https://graph.microsoft.com/v1.0/me/calendars/{Segment(id, "calendarId")}"
            : "https://graph.microsoft.com/v1.0/me";
        return operation switch
        {
            CalendarOperations.List when Optional(args, "start") is { } start && Optional(args, "end") is { } end =>
                new(HttpMethod.Get, $"{calendar}/calendarView?startDateTime={Uri.EscapeDataString(start)}&endDateTime={Uri.EscapeDataString(end)}&$top={Limit(args, "max", 50, 250)}"),
            CalendarOperations.List => new(HttpMethod.Get, $"{calendar}/events?$top={Limit(args, "max", 50, 250)}"),
            CalendarOperations.Read => new(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/events/{Segment(Required(args, "id"), "id")}"),
            CalendarOperations.Create => new(HttpMethod.Post, $"{calendar}/events", new
            {
                subject = SingleLine(Required(args, "summary"), "summary"),
                body = new { contentType = "Text", content = Optional(args, "description") ?? string.Empty },
                start = new { dateTime = Utc(Required(args, "start"), "start"), timeZone = "UTC" },
                end = new { dateTime = Utc(Required(args, "end"), "end"), timeZone = "UTC" },
                attendees = Addresses(args, "attendees", required: false).Select(a => new { emailAddress = new { address = a }, type = "required" }).ToArray(),
            }),
            _ => throw new ArgumentException($"Unknown operation: {operation}"),
        };
    }

    /// <summary>Graph pairs a zone-less time with a time zone name, so offsets are normalized to UTC.</summary>
    private static string Utc(string value, string name) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"{name} must be an ISO 8601 date-time.");
}
