using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;

namespace Capybara.Policy.Gate;

/// <summary>One pending tool call as seen by <c>OnPreToolUse</c> / <c>OnPermissionRequest</c>.</summary>
public sealed record ToolCallContext(
    AgentDefinition Agent,
    string SessionId,
    string Tool,
    JsonElement? Args,
    MessageOrigin Origin,
    string? TraceId = null);
