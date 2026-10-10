using Capybara.Connections.Broker;
using Capybara.Connections.Model;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Audit;
using Capybara.Testing;

namespace Capybara.Connections.Tests;

/// <summary>Freeze races and audit-log failures around the provider call (FR-SAF-06, FR-CON-10).</summary>
public sealed class BrokerDurabilityTests
{
    private static readonly BrokerCall Read = new("ea", "email", "read", "{\"id\":\"1\"}");

    [Fact]
    public async Task Freeze_during_token_refresh_stops_the_call_before_the_connector()
    {
        BrokerHarness? self = null;
        using var h = self = new BrokerHarness(tokenEndpoint: _ =>
        {
            self!.Freeze.FreezeAsync("kill switch").AsTask().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json("""{"access_token":"ya29.new-token-value","expires_in":3600}""");
        });
        await h.AuthorizeExpiringAsync();

        var result = await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        Assert.True(h.Freeze.IsFrozen);
        Assert.False(result.Allowed);
        Assert.Contains("frozen", result.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Connector.Calls);
        Assert.Contains("frozen", Assert.Single(h.BrokerCalls).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Kind == "broker_attempt");
    }

    [Fact]
    public async Task Freeze_reports_provider_calls_already_in_flight()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        FreezeOutcome? during = null;
        h.Connector.Behavior = (_, _) =>
        {
            during = h.Freeze.FreezeAsync("kill switch").AsTask().GetAwaiter().GetResult();
            return new BrokerResult(true, "sent", Taint.Clean, 4);
        };

        await h.Broker.InvokeAsync(Read, TestContext.Current.CancellationToken);
        var after = await h.Freeze.FreezeAsync("again");

        Assert.Equal(1, during!.InFlightCalls);
        Assert.Equal(0, after.InFlightCalls);
        Assert.Equal(0, h.Freeze.InFlightCalls);
        Assert.Contains("inFlightCalls=1", h.Audit.Entries.First(e => e.Kind == "broker_freeze").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_log_down_means_the_provider_is_never_contacted()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        var broker = h.CreateBroker(new FailingAudit(h.Audit, _ => true));

        var first = await broker.InvokeAsync(Read, TestContext.Current.CancellationToken);
        var retry = await broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        Assert.False(first.Allowed);
        Assert.Contains("not attempted", first.Reason, StringComparison.Ordinal);
        Assert.False(retry.Allowed);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Attempt_write_failure_denies_unsent()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        var broker = h.CreateBroker(new FailingAudit(h.Audit, r => r.Kind == "broker_attempt"));

        var result = await broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Empty(h.Connector.Calls);
    }

    [Fact]
    public async Task Outcome_audit_failure_after_success_returns_the_real_result()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        var broker = h.CreateBroker(new FailingAudit(h.Audit, r => r.Kind == "broker_call"));

        var result = await broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal("action completed but outcome audit failed", result.Reason);
        Assert.Contains("message body", result.Text, StringComparison.Ordinal);
        Assert.Single(h.Connector.Calls);
        var attempt = Assert.Single(h.Audit.Entries);
        Assert.Equal(("broker_attempt", "attempt", "email_read"), (attempt.Kind, attempt.Decision, attempt.Tool));
    }

    [Fact]
    public async Task Outcome_audit_failure_after_a_connector_failure_keeps_the_real_reason()
    {
        using var h = new BrokerHarness();
        await h.AuthorizeAsync();
        h.Connector.Behavior = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "reset");
        var broker = h.CreateBroker(new FailingAudit(h.Audit, r => r.Kind == "broker_call"));

        var result = await broker.InvokeAsync(Read, TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("connector unreachable (ConnectionError); outcome unknown; outcome audit failed", result.Reason);
    }

    [Fact]
    public async Task Unfreeze_that_cannot_be_audited_leaves_the_broker_frozen()
    {
        using var dir = new TempDirectory();
        var inner = new InMemoryAuditLog();
        var failing = false;
        using var freeze = new BrokerFreeze(Path.Combine(dir.Path, "broker.frozen"), new FailingAudit(inner, _ => failing));
        await freeze.FreezeAsync("kill switch");
        failing = true;

        await Assert.ThrowsAsync<IOException>(async () => await freeze.UnfreezeAsync(TestContext.Current.CancellationToken));

        Assert.True(freeze.IsFrozen);
        Assert.True(File.Exists(Path.Combine(dir.Path, "broker.frozen")));
        Assert.Equal(["broker_freeze"], inner.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Unfreeze_whose_marker_cannot_be_removed_is_recorded_and_stays_frozen()
    {
        using var dir = new TempDirectory();
        var marker = Directory.CreateDirectory(Path.Combine(dir.Path, "broker.frozen")).FullName;
        await File.WriteAllTextAsync(Path.Combine(marker, "keep"), "x", TestContext.Current.CancellationToken);
        var audit = new InMemoryAuditLog();
        using var freeze = new BrokerFreeze(marker, audit);
        await freeze.FreezeAsync("kill switch");

        await Assert.ThrowsAnyAsync<Exception>(async () => await freeze.UnfreezeAsync(TestContext.Current.CancellationToken));

        Assert.True(freeze.IsFrozen);
        Assert.Equal(["broker_freeze", "broker_unfreeze", "broker_unfreeze_failed"], audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Freeze_survives_any_audit_failure()
    {
        using var dir = new TempDirectory();
        using var freeze = new BrokerFreeze(Path.Combine(dir.Path, "broker.frozen"), new FailingAudit(new InMemoryAuditLog(), _ => true, () => new ObjectDisposedException("audit")));

        var outcome = await freeze.FreezeAsync("kill switch");

        Assert.True(freeze.IsFrozen);
        Assert.Contains("ObjectDisposedException", outcome.Error, StringComparison.Ordinal);
    }

    private sealed class FailingAudit(IAuditLog inner, Func<AuditRecord, bool> fail, Func<Exception>? error = null) : IAuditLog
    {
        public ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
            fail(record) ? throw (error?.Invoke() ?? new IOException("disk full")) : inner.AppendAsync(record, cancellationToken);

        public IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken cancellationToken = default) => inner.ReadAllAsync(cancellationToken);

        public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default) => inner.VerifyAsync(cancellationToken);
    }
}
