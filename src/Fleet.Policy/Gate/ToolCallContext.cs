using System.Text.Json;
using Fleet.Contracts.Agents;
using Fleet.Contracts.Messaging;

namespace Fleet.Policy.Gate;

/// <summary>One pending tool call as seen by <c>OnPreToolUse</c> / <c>OnPermissionRequest</c>.</summary>
public sealed record ToolCallContext(
    AgentDefinition Agent,
    string SessionId,
    string Tool,
    JsonElement? Args,
    MessageOrigin Origin,
    string? TraceId = null);
