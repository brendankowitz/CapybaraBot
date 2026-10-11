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

    /// <summary>Agents a human paused from a client; re-applied on start (FR-SAF-06).</summary>
    public string PausedAgents => Path.Combine(Root, "policy", "paused-agents.json");

    /// <summary>Tainted session ids, so resumed sessions stay tainted across restarts (FR-SAF-03).</summary>
    public string Taint => Path.Combine(Root, "policy", "taint.jsonl");

    public string CostLedger => Path.Combine(Root, "observability", "cost-ledger.jsonl");

    public string Alerts => Path.Combine(Root, "observability", "alerts.jsonl");

    public string Memory => Path.Combine(Root, "memory");

    public string Inbox => Path.Combine(Root, "inbox");

    public string Tasks => Path.Combine(Root, "tasks");

    public string Proposals => Path.Combine(Root, "proposals");

    public string SchedulingDatabase => Path.Combine(Root, "scheduling", "scheduling.sqlite");

    public string WebPushSubscriptions => Path.Combine(Root, "channels", "web-push-subscriptions.json");

    /// <summary>The user's notification toggles and quiet hours as saved from the PWA.</summary>
    public string NotificationSettings => Path.Combine(Root, "channels", "notification-settings.json");

    /// <summary>Per-agent chat logs; under the state root, so no agent can write a line the clients show as the user's.</summary>
    public string Chat => Path.Combine(Root, "chat");

    /// <summary>Private scratch space where config changes are validated before they touch the live tree.</summary>
    public string ConfigScratch => Path.Combine(Root, "tmp", "config-validation");

    /// <summary>Per-agent srt settings; outside every agent directory so no agent can rewrite its own policy.</summary>
    public string SandboxSettings => Path.Combine(Root, "srt");
}
