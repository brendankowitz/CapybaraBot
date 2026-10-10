using Fleet.Contracts.Agents;
using Fleet.Contracts.Routines;
using Fleet.Policy.Rules;

namespace Fleet.Orchestration.Configuration;

/// <summary>The validated, user-owned fleet configuration.</summary>
public sealed record FleetConfiguration(
    IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyList<PolicyRule> Rules,
    IReadOnlyList<RoutineSpec> Routines);
