using Fleet.Contracts.Approvals;

namespace Fleet.Gateway.Endpoints;

public sealed record ApprovalDecisionRequest(string Nonce, ApprovalDecision Decision);

public sealed record KillSwitchRequest(string Reason);

public sealed record TurnRequest(string Prompt);

public sealed record PendingChangeDecisionRequest(bool Apply);

public sealed record AgentStatus(string Id, string Name, string Job, string Model, bool Paused, bool BudgetExhausted);
