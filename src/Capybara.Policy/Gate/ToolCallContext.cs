using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Policy.Gate;

/// <summary>One pending tool call as seen by <c>OnPreToolUse</c> / <c>OnPermissionRequest</c>.</summary>
/// <remarks>
/// <para>
/// <c>DryRun</c> marks a call from a routine dry run (FR-RTN-08): anything that would need a human is denied instead,
/// and no approval request is raised, so nobody is prompted by a test.
/// </para>
/// <para>
/// <c>ApprovalBoundary</c> holds the tool patterns the routine that triggered the turn declared as its approval
/// boundary (FR-RTN-02): for that run they are approval-required, like the agent's own approval-required tools.
/// </para>
/// </remarks>
public sealed record ToolCallContext(
    AgentDefinition Agent,
    string SessionId,
    string Tool,
    JsonElement? Args,
    MessageOrigin Origin,
    string? TraceId = null,
    bool DryRun = false,
    IReadOnlyList<string>? ApprovalBoundary = null);
