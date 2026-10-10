using System.Net;
using System.Text.Json;
using Capybara.Connections.Connectors;
using Capybara.Connections.Model;
using Capybara.Contracts.Messaging;

namespace Capybara.Connections.Tests;

public sealed class ConnectorTests
{
    [Fact]
    public async Task Gmail_read_uses_bearer_and_marks_output_tainted()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{\"id\":\"msg\"}"));
        var connector = new GmailConnector(new HttpClient(handler), ConnectionDefinition.Create("email", ConnectorKind.Gmail));
        using var args = JsonDocument.Parse("{\"id\":\"msg\"}");

        var result = await connector.InvokeAsync(MailOperations.Read, args.RootElement, "runtime-token", TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal(Taint.Tainted, result.Taint);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/messages/msg?format=full", request.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Gmail_send_builds_the_message_only_from_declared_fields()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json("{}");
        });
        var connector = new GmailConnector(new HttpClient(handler), ConnectionDefinition.Create("email", ConnectorKind.Gmail));
        using var args = JsonDocument.Parse("""{"to":["bob@example.com"],"subject":"Hi","body":"Hello","raw":"smuggled"}""");

        var result = await connector.InvokeAsync(MailOperations.Send, args.RootElement, "t", TestContext.Current.CancellationToken);

        Assert.Equal(Taint.Clean, result.Taint);
        using var sent = JsonDocument.Parse(body!);
        var raw = sent.RootElement.GetProperty("raw").GetString()!;
        var mime = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight((raw.Length + 3) / 4 * 4, '=')));
        Assert.StartsWith("To: bob@example.com\r\n", mime, StringComparison.Ordinal);
        Assert.DoesNotContain("smuggled", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"to":["bob@example.com, eve@evil.test"],"subject":"Hi","body":"x"}""")]
    [InlineData("""{"to":["bob@example.com"],"subject":"Hi\r\nBcc: eve@evil.test","body":"x"}""")]
    [InlineData("""{"subject":"Hi","body":"x"}""")]
    public async Task Mail_send_rejects_smuggled_recipients_and_header_injection(string json)
    {
        var connector = new GraphMailConnector(new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"))), ConnectionDefinition.Create("email", ConnectorKind.GraphMail));
        using var args = JsonDocument.Parse(json);

        await Assert.ThrowsAsync<ArgumentException>(async () => await connector.InvokeAsync(MailOperations.Send, args.RootElement, "t", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHub_pull_request_head_comes_from_the_constrained_branch_argument()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
        });
        var connector = new GitHubConnector(new HttpClient(handler), ConnectionDefinition.Create("github", ConnectorKind.GitHub));
        using var args = JsonDocument.Parse("""{"repo":"owner/repo","branch":"agent/fix","base":"main","title":"Fix","head":"main"}""");

        var result = await connector.InvokeAsync(GitHubConnector.CreatePullRequest, args.RootElement, "t", TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("agent/fix", sent.RootElement.GetProperty("head").GetString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/owner/repo/pulls", request.RequestUri!.AbsoluteUri);
        Assert.Contains("capybara-broker", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"repo":"owner/../x","path":"a"}""")]
    [InlineData("""{"repo":"owner/repo","path":"../secrets"}""")]
    [InlineData("""{"repo":"owner/repo/extra","path":"a"}""")]
    public async Task GitHub_rejects_path_traversal(string json)
    {
        var connector = new GitHubConnector(new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"))), ConnectionDefinition.Create("github", ConnectorKind.GitHub));
        using var args = JsonDocument.Parse(json);

        await Assert.ThrowsAsync<ArgumentException>(async () => await connector.InvokeAsync(GitHubConnector.ReadFile, args.RootElement, "t", TestContext.Current.CancellationToken));
    }

[Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".GitHub/Workflows/ci.yml")]
    [InlineData(".github/actions/setup/action.yml")]
    public async Task GitHub_refuses_to_commit_workflow_or_action_definitions(string path)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"));
        var connector = new GitHubConnector(new HttpClient(handler), ConnectionDefinition.Create("github", ConnectorKind.GitHub));
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { repo = "owner/repo", branch = "agent/x", path, content = "on: push", message = "m" }));

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await connector.InvokeAsync(GitHubConnector.CommitFile, args.RootElement, "t", TestContext.Current.CancellationToken));

        Assert.Contains("protected", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GitHub_commits_ordinary_files_on_the_branch()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("{}"));
        var connector = new GitHubConnector(new HttpClient(handler), ConnectionDefinition.Create("github", ConnectorKind.GitHub));
        using var args = JsonDocument.Parse("""{"repo":"owner/repo","branch":"agent/x","path":"src/a b.cs","content":"x","message":"m"}""");

        await connector.InvokeAsync(GitHubConnector.CommitFile, args.RootElement, "t", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Put, "https://api.github.com/repos/owner/repo/contents/src/a%20b.cs"), (request.Method, request.RequestUri!.AbsoluteUri));
    }

    [Fact]
    public async Task Graph_calendar_normalizes_event_times_to_utc()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json("{}");
        });
        var connector = new GraphCalendarConnector(new HttpClient(handler), ConnectionDefinition.Create("calendar", ConnectorKind.GraphCalendar));
        using var args = JsonDocument.Parse("""{"summary":"Sync","start":"2026-10-10T09:00:00-07:00","end":"2026-10-10T09:30:00-07:00"}""");

        await connector.InvokeAsync(CalendarOperations.Create, args.RootElement, "t", TestContext.Current.CancellationToken);

        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("2026-10-10T16:00:00", sent.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
    }

    [Fact]
    public void Connection_ids_cannot_make_tool_names_ambiguous()
    {
        Assert.Throws<ArgumentException>(() => ConnectionDefinition.Create("my_mail", ConnectorKind.Gmail));
        Assert.Throws<ArgumentException>(() => ConnectionDefinition.Create("Mail", ConnectorKind.Gmail));
        Assert.Equal(["https://www.googleapis.com/auth/gmail.readonly", "https://www.googleapis.com/auth/gmail.compose"], ConnectionDefinition.Create("email", ConnectorKind.Gmail).Scopes);
    }

    [Fact]
    public void Registry_rejects_duplicate_connection_ids()
    {
        var http = new HttpClient();
        var connection = ConnectionDefinition.Create("email", ConnectorKind.Gmail);

        Assert.Throws<ArgumentException>(() => new ConnectorRegistry([new GmailConnector(http, connection), new GraphMailConnector(http, ConnectionDefinition.Create("email", ConnectorKind.GraphMail))]));
    }
}
