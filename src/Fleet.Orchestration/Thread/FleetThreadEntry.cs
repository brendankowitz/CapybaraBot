using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;

namespace Fleet.Orchestration.Thread;

/// <summary>One line of the user-visible log of agent-to-agent traffic.</summary>
public sealed record FleetThreadEntry(
    DateTimeOffset At,
    AgentId From,
    AgentId To,
    A2AOperation Operation,
    string Summary,
    string TraceId,
    int Depth,
    Taint Taint,
    bool Rejected = false);
