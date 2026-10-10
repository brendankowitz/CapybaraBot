namespace Capybara.Gateway.Composition;

/// <summary>
/// Where capybarad keeps each piece of mutable state (FR-HST-02). Everything sits under the state root, which the srt
/// sandbox denies to every agent (only an agent's own workspace and <c>BaseDirectory</c> are re-allowed), so journals
/// such as approvals, the audit log and learned rules are never agent-readable (FR-SAF-07).
/// </summary>
internal sealed record StateLayout(string Root)
{
    public string Audit => Path.Combine(Root, "audit", "audit.jsonl");

    public string Approvals => Path.Combine(Root, "policy", "approvals.jsonl");

    public string LearnedRules => Path.Combine(Root, "policy", "learned-rules.json");

    public string KillSwitch => Path.Combine(Root, "policy", "kill-switch.json");

    public string CostLedger => Path.Combine(Root, "observability", "cost-ledger.jsonl");

    public string Alerts => Path.Combine(Root, "observability", "alerts.jsonl");

    public string Memory => Path.Combine(Root, "memory");

    public string Inbox => Path.Combine(Root, "inbox");

    public string Tasks => Path.Combine(Root, "tasks");

    public string Proposals => Path.Combine(Root, "proposals");

    public string SchedulingDatabase => Path.Combine(Root, "scheduling", "scheduling.sqlite");

    public string WebPushSubscriptions => Path.Combine(Root, "channels", "web-push-subscriptions.json");

    /// <summary>Per-agent srt settings; outside every agent directory so no agent can rewrite its own policy.</summary>
    public string SandboxSettings => Path.Combine(Root, "srt");
}
