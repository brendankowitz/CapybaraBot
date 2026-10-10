using Capybara.Connections.Broker;
using Capybara.Connections.Model;
using Capybara.Contracts.Connections;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Audit;
using Capybara.Testing;

namespace Capybara.Connections.Tests;

public sealed class BrokerTests
{
    [Fact]
    public async Task Allowed_call_is_redacted_and_audited_with_hash_size_and_taint()
    {
        using var h = new BrokerHarness();
        var token = await h.AuthorizeAsync();

        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}", "agent:ea:main"), TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal(Taint.Tainted, result.Taint);
        Assert.DoesNotContain(token, result.Text, StringComparison.Ordinal);
        var entry = Assert.Single(h.Audit.Entries);
        Assert.Equal(("broker_call", "allow", "ea", "email_read", "agent:ea:main"), (entry.Kind, entry.Decision, entry.Agent, entry.Tool, entry.SessionId));
        Assert.Equal(64, entry.ArgsHash!.Length);
        Assert.Contains("taint=Tainted", entry.Detail, StringComparison.Ordinal);
        Assert.Contains($"size={result.Size}", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_shaped_secrets_in_connector_output_are_redacted()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        h.Connector.Behavior = (_, _) => new BrokerResult(true, "repo secret ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 and AKIAABCDEFGHIJKLMNOP", Taint.Tainted, 0);

        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("ghp_", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", result.Text, StringComparison.Ordinal);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(result.Text), result.Size);
    }

    [Fact]
    public async Task Every_call_writes_exactly_one_audit_record_whatever_the_outcome()
    {
        using var h = new BrokerHarness();
        var ct = TestContext.Current.CancellationToken;

        await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"), ct);          // no credential yet
        await h.AuthorizeAsync();
        await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "send", "{}"), ct);                       // not granted
        await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{not json"), ct);                // malformed
        await h.Broker.InvokeAsync(new BrokerCall("research", "email", "read", "{\"id\":\"1\"}"), ct);    // no grants
        await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"), ct);          // allowed

        Assert.Equal(["deny", "deny", "deny", "deny", "allow"], h.Audit.Entries.Select(e => e.Decision));
        Assert.Contains("not authorized", h.Audit.Entries[0].Detail, StringComparison.Ordinal);
        Assert.Contains("no matching grant", h.Audit.Entries[1].Detail, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", h.Audit.Entries[2].Detail, StringComparison.Ordinal);
        Assert.Single(h.Connector.Calls);
    }

    [Fact]
    public async Task Invalid_connector_arguments_are_denied_not_thrown()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        h.Connector.Behavior = (_, _) => throw new ArgumentException("Missing argument: id");

        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{}"), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Contains("Missing argument: id", result.Reason, StringComparison.Ordinal);
        Assert.Equal("deny", Assert.Single(h.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Grant_constraints_deny_before_the_connector_runs()
    {
        using var h = new BrokerHarness(grants:
        [
            new Grant
            {
                Connection = "email",
                Operations = ["send"],
                Constraints = new Dictionary<string, IReadOnlyList<string>> { ["to"] = ["*@example.com"] },
            },
        ]);
        await h.AuthorizeAsync();
        var ct = TestContext.Current.CancellationToken;

        var denied = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "send", """{"to":["eve@evil.test"],"subject":"s","body":"b"}"""), ct);
        var allowed = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "send", """{"to":["bob@example.com"],"subject":"s","body":"b"}"""), ct);

        Assert.False(denied.Allowed);
        Assert.True(allowed.Allowed);
        Assert.Single(h.Connector.Calls);
    }

    [Fact]
    public async Task Frozen_broker_denies_everything_until_unfrozen_and_audits_both()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        var ct = TestContext.Current.CancellationToken;
        var call = new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}");

        await h.Freeze.FreezeAsync("kill switch engaged from phone");
        var frozen = await h.Broker.InvokeAsync(call, ct);
        await h.Freeze.UnfreezeAsync(ct);
        var thawed = await h.Broker.InvokeAsync(call, ct);

        Assert.False(frozen.Allowed);
        Assert.Contains("frozen", frozen.Reason, StringComparison.Ordinal);
        Assert.True(thawed.Allowed);
        Assert.Single(h.Connector.Calls);
        Assert.Equal(["broker_freeze", "broker_call", "broker_unfreeze", "broker_call"], h.Audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Freeze_survives_a_broker_restart()
    {
        using var h = new BrokerHarness();
        await h.Freeze.FreezeAsync("kill switch");

        using var restarted = new BrokerFreeze(h.FreezeMarker, new InMemoryAuditLog());

        Assert.True(restarted.IsFrozen);
        Assert.Equal("kill switch", restarted.Reason);
        await restarted.UnfreezeAsync(TestContext.Current.CancellationToken);
        Assert.False(new BrokerFreeze(h.FreezeMarker, new InMemoryAuditLog()).IsFrozen);
    }

    [Fact]
    public async Task Broker_calls_land_in_a_verifiable_hash_chained_audit_file()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "audit", "broker-audit.jsonl");
        using var log = new FileAuditLog(path, TimeProvider.System);
        using var h = new BrokerHarness();
        var broker = new ConnectionBroker(new Grants.GrantEnforcer(h.Grants), h.Catalog, h.Registry, h.Credentials, log, h.Freeze);
        await h.AuthorizeAsync();
        var ct = TestContext.Current.CancellationToken;

        await broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"), ct);
        await broker.InvokeAsync(new BrokerCall("ea", "email", "send", "{}"), ct);

        var entries = new List<AuditEntry>();
        await foreach (var entry in log.ReadAllAsync(ct))
        {
            entries.Add(entry);
        }

        Assert.Equal(["allow", "deny"], entries.Select(e => e.Decision));
        Assert.True(await log.VerifyAsync(ct));
    }

    [Fact]
    public async Task Freeze_holds_in_memory_and_is_audited_when_the_marker_cannot_be_written()
    {
        using var scratch = new TempDirectory();
        var blocker = Path.Combine(scratch.Path, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        using var h = new BrokerHarness(freezeMarker: Path.Combine(blocker, "broker.frozen"));
        await h.AuthorizeAsync();

        var outcome = await h.Freeze.FreezeAsync("kill switch");
        var result = await h.Broker.InvokeAsync(new BrokerCall("ea", "email", "read", "{\"id\":\"1\"}"), TestContext.Current.CancellationToken);

        Assert.False(outcome.Durable);
        Assert.Contains("could not be persisted", outcome.Error, StringComparison.Ordinal);
        Assert.True(h.Freeze.IsFrozen);
        Assert.False(result.Allowed);
        Assert.Empty(h.Connector.Calls);
        var audited = Assert.Single(h.Audit.Entries, e => e.Kind == "broker_freeze");
        Assert.Contains("could not be persisted", audited.Detail, StringComparison.Ordinal);
    }
}