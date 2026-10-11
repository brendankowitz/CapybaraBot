using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Policy.Rules;

namespace Capybara.Orchestration.Configuration;

/// <summary>The validated, user-owned fleet configuration.</summary>
public sealed record CapybaraConfiguration(
    IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyList<PolicyRule> Rules,
    IReadOnlyList<RoutineSpec> Routines);
