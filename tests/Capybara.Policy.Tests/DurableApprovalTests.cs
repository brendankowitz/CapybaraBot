using Capybara.Contracts.Agents;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Messaging;
using Capybara.Policy.Approvals;
using Capybara.Policy.Audit;
using Capybara.Policy.Rules;
using Capybara.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Policy.Tests;

public sealed class DurableApprovalTests : IDisposable
{
    private static readonly AgentId Ea = AgentId.Parse("ea");
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAuditLog _audit = new();

    private string JournalPath => Path.Combine(_dir.Path, "state", "approvals.jsonl");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Pending_approvals_reload_and_can_be_decided_after_a_restart()
    {
        var before = NewService();
        var request = before.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.Low, MessageOrigin.User, "Escalate", "unsure");

        var after = NewService();

        var reloaded = Assert.Single(after.Pending());
        Assert.Equal(request, reloaded);
        Assert.Equal(ApprovalOutcome.InvalidNonce, after.Decide(request.Id, "wrong", ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.Equal(ApprovalOutcome.Accepted, after.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.True(after.TryConsume(Ea, "s1", "email_send", "hash"));
    }

    [Fact]
    public void Approvals_that_expire_while_down_fail_closed_and_are_audited()
    {
        var before = NewService();
        var request = before.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.Low, MessageOrigin.Event);
        _time.Advance(TimeSpan.FromMinutes(11));

        var after = NewService();
        ApprovalRequest? expiredEvent = null;
        after.Expired += (_, r) => expiredEvent = r;

        Assert.Equal(ApprovalOutcome.Expired, after.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));
        Assert.Empty(after.Pending());
        Assert.False(after.TryConsume(Ea, "s1", "email_send", "hash"));
        Assert.Equal(request.Id, expiredEvent?.Id);
        Assert.Single(_audit.Entries, e => e.Kind == "approval" && e.Decision == "expired" && e.Detail == $"approval {request.Id}");
    }

    [Fact]
    public void Expiry_is_journaled_so_it_is_not_reported_twice_across_restarts()
    {
        NewService().Request(Ea, "s1", "x", "hash", "preview", RiskLevel.Low, MessageOrigin.Event);
        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, NewService().Sweep());
        Assert.Equal(0, NewService().Sweep());
        Assert.Single(_audit.Entries, e => e.Decision == "expired");
    }

    [Fact]
    public void Single_use_holds_across_a_restart()
    {
        var before = NewService();
        var request = before.Request(Ea, "s1", "email_send", "hash", "preview", RiskLevel.Low, MessageOrigin.User);
        before.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);
        Assert.True(before.TryConsume(Ea, "s1", "email_send", "hash"));

        var after = NewService();

        Assert.False(after.TryConsume(Ea, "s1", "email_send", "hash"));
        Assert.Null(after.Get(request.Id));
    }

    [Fact]
    public void Decisions_are_appended_to_the_audit_log()
    {
        var service = NewService();
        var allow = service.Request(Ea, "s1", "a", "h1", "p", RiskLevel.Low, MessageOrigin.User);
        var always = service.Request(Ea, "s1", "b", "h2", "p", RiskLevel.Low, MessageOrigin.User);
        var deny = service.Request(Ea, "s1", "c", "h3", "p", RiskLevel.Low, MessageOrigin.User);

        service.Decide(allow.Id, allow.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);
        service.Decide(always.Id, always.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User);
        service.Decide(deny.Id, deny.Nonce, ApprovalDecision.Deny, MessageOrigin.User);
        service.Decide(deny.Id, deny.Nonce, ApprovalDecision.Deny, MessageOrigin.User); // already decided: not audited again

        Assert.Equal(
            ["allow_once:a", "always_allow:b", "deny:c"],
            _audit.Entries.Where(e => e.Kind == "approval").Select(e => $"{e.Decision}:{e.Tool}"));
        var entry = _audit.Entries[0];
        Assert.Equal("ea", entry.Agent);
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("h1", entry.ArgsHash);
    }

    [Fact]
    public void A_torn_journal_tail_is_dropped_and_later_writes_are_readable()
    {
        var before = NewService();
        var kept = before.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.User);
        File.AppendAllText(JournalPath, "{\"id\":\"deadbeef\",\"nonce\":\"ab");

        var recovered = NewService();
        Assert.Equal(kept.Id, Assert.Single(recovered.Pending()).Id);
        var next = recovered.Request(Ea, "s1", "b", "h2", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Equal(2, NewService().Pending().Count);
        Assert.NotNull(NewService().Get(next.Id));
    }

    [Fact]
    public void Always_allow_survives_a_restart_through_the_learned_rule_store()
    {
        var rulesPath = Path.Combine(_dir.Path, "state", "learned-rules.json");
        var store = new LearnedRuleStore(rulesPath);
        var engine = store.CreateEngine([new PolicyRule { Effect = RuleEffect.Ask, Tool = "email_send" }]);
        var service = new ApprovalService(engine, new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), _audit);
        var request = service.Request(Ea, "s1", "calendar_read", "h", "p", RiskLevel.Low, MessageOrigin.User);
        service.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User);

        var restarted = new LearnedRuleStore(rulesPath).CreateEngine([new PolicyRule { Effect = RuleEffect.Ask, Tool = "email_send" }]);

        var match = restarted.Evaluate("ea", "calendar_read", null);
        Assert.Equal(RuleEffect.Allow, match?.Rule.Effect);
        Assert.Equal(2, restarted.Rules.Count);
        Assert.Single(restarted.LearnedRules);
        Assert.Equal(ApprovalStatus.Approved, NewService().Get(request.Id)?.Status);
    }

    [Fact]
    public void Learned_rules_round_trip_all_fields()
    {
        var path = Path.Combine(_dir.Path, "rules.json");
        var store = new LearnedRuleStore(path);
        var rule = new PolicyRule
        {
            Effect = RuleEffect.AllowUnlessTainted,
            Agent = "ea",
            Tool = "web_fetch",
            Argument = "url",
            ArgumentPattern = "https://*.example.com/*",
            DestinationAllowlist = ["https://a.example.com/x"],
            Reason = "r",
        };
        store.Save([rule]);

        var loaded = Assert.Single(new LearnedRuleStore(path).Load());

        Assert.Equal(rule.Effect, loaded.Effect);
        Assert.Equal(rule.ArgumentPattern, loaded.ArgumentPattern);
        Assert.Equal(rule.DestinationAllowlist, loaded.DestinationAllowlist);
        Assert.Empty(new LearnedRuleStore(Path.Combine(_dir.Path, "missing.json")).Load());
    }

    [Fact]
    public async Task The_real_audit_log_chain_stays_valid_with_approval_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = new FileAuditLog(Path.Combine(_dir.Path, "audit.jsonl"), _time);
        var service = new ApprovalService(new RulesEngine([]), new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), log);
        var request = service.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.User);
        service.Decide(request.Id, request.Nonce, ApprovalDecision.Deny, MessageOrigin.User);

        Assert.True(await log.VerifyAsync(ct));
        Assert.Single(await log.ReadAllAsync(ct).ToListAsync(ct), e => e.Kind == "approval" && e.Decision == "deny");
    }

    [Fact]
    public void Audit_is_written_before_the_approval_becomes_consumable()
    {
        ApprovalService? service = null;
        string? statusAtAppend = null;
        var observing = new CallbackAuditLog(record =>
        {
            if (record.Decision == "allow_once")
            {
                statusAtAppend = service!.Get(record.Detail![9..])?.Status.ToString();
                Assert.False(service.TryConsume(Ea, "s1", "a", "h"));
            }
        });
        service = new ApprovalService(new RulesEngine([]), new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), observing);
        var request = service.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Equal(ApprovalOutcome.Accepted, service.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));

        Assert.Equal("Pending", statusAtAppend);
        Assert.True(service.TryConsume(Ea, "s1", "a", "h"));
    }

    [Fact]
    public void A_failed_audit_append_commits_nothing_so_the_approval_is_not_consumable()
    {
        var failing = new CallbackAuditLog(_ => throw new IOException("disk full"));
        var service = new ApprovalService(new RulesEngine([]), new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), failing);
        var request = service.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Throws<IOException>(() => service.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User));

        Assert.False(service.TryConsume(Ea, "s1", "a", "h"));
        Assert.Equal(ApprovalStatus.Pending, service.Get(request.Id)?.Status);
        Assert.Equal(ApprovalStatus.Pending, NewService().Get(request.Id)?.Status);
    }

    [Fact]
    public void A_failed_audit_append_does_not_create_an_always_allow_rule()
    {
        var rules = new RulesEngine([]);
        var failing = new CallbackAuditLog(_ => throw new IOException("disk full"));
        var service = new ApprovalService(rules, new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), failing);
        var request = service.Request(Ea, "s1", "calendar_read", "h", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Throws<IOException>(() => service.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User));

        Assert.Empty(rules.LearnedRules);
    }

    [Fact]
    public void An_expiry_whose_audit_fails_still_fails_closed_and_is_re_audited_after_restart()
    {
        var flaky = new CallbackAuditLog(record =>
        {
            if (record.Decision == "expired")
            {
                throw new IOException("disk full");
            }
        });
        var service = new ApprovalService(new RulesEngine([]), new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), flaky);
        var request = service.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.User);
        service.Decide(request.Id, request.Nonce, ApprovalDecision.AllowOnce, MessageOrigin.User);
        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(service.TryConsume(Ea, "s1", "a", "h"));
        Assert.Equal(ApprovalStatus.Expired, service.Get(request.Id)?.Status);

        var restarted = NewService();
        Assert.Equal(1, restarted.Sweep());
        Assert.False(restarted.TryConsume(Ea, "s1", "a", "h"));
        Assert.Single(_audit.Entries, e => e.Decision == "expired");
    }

    [Fact]
    public void A_throwing_subscriber_cannot_suppress_the_audit_the_rule_or_other_subscribers()
    {
        var rules = new RulesEngine([]);
        var service = new ApprovalService(rules, new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), _audit);
        var secondSubscriberRan = false;
        service.Decided += (_, _) => throw new InvalidOperationException("notifier down");
        service.Decided += (_, _) => secondSubscriberRan = true;
        var request = service.Request(Ea, "s1", "calendar_read", "h", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Equal(ApprovalOutcome.Accepted, service.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User));

        Assert.True(secondSubscriberRan);
        Assert.Single(rules.LearnedRules);
        Assert.Single(_audit.Entries, e => e.Decision == "always_allow");
        Assert.Single(_audit.Entries, e => e.Decision == "handler_error" && e.Detail!.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(ApprovalStatus.Approved, service.Get(request.Id)?.Status);
    }

    [Fact]
    public void A_throwing_expiry_subscriber_still_leaves_the_expiry_audited()
    {
        var service = NewService();
        service.Expired += (_, _) => throw new InvalidOperationException("notifier down");
        service.Request(Ea, "s1", "a", "h", "p", RiskLevel.Low, MessageOrigin.Event);
        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, service.Sweep());

        Assert.Single(_audit.Entries, e => e.Decision == "expired");
        Assert.Single(_audit.Entries, e => e.Decision == "handler_error");
        Assert.Empty(service.Pending());
    }

    [Fact]
    public void A_throwing_learned_rule_save_does_not_undo_the_committed_decision()
    {
        var blocker = Path.Combine(_dir.Path, "blocker");
        File.WriteAllText(blocker, "a file where the rule directory should be");
        var store = new LearnedRuleStore(Path.Combine(blocker, "rules.json"));
        var engine = store.CreateEngine([]);
        var service = new ApprovalService(engine, new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), _audit);
        var request = service.Request(Ea, "s1", "calendar_read", "h", "p", RiskLevel.Low, MessageOrigin.User);

        Assert.Equal(ApprovalOutcome.Accepted, service.Decide(request.Id, request.Nonce, ApprovalDecision.AlwaysAllow, MessageOrigin.User));

        Assert.Single(engine.LearnedRules);
        Assert.Single(_audit.Entries, e => e.Decision == "handler_error" && e.Detail!.Contains("always-allow rule", StringComparison.Ordinal));
    }

    private ApprovalService NewService() =>
        new(new RulesEngine([]), new ApprovalOptions(), _time, new FileApprovalStore(JournalPath), _audit);

    private sealed class CallbackAuditLog(Action<AuditRecord> onAppend) : IAuditLog
    {
        public ValueTask<AuditEntry> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            onAppend(record);
            return ValueTask.FromResult(new AuditEntry
            {
                Sequence = 0,
                At = DateTimeOffset.UnixEpoch,
                Kind = record.Kind,
                Decision = record.Decision,
                PreviousHash = string.Empty,
            });
        }

        public IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<AuditEntry>();

        public ValueTask<bool> VerifyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }}
