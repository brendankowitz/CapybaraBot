using Capybara.Contracts.Agents;

namespace Capybara.Channels;

/// <summary>An <c>ask_user</c> question waiting for the user's answer (FR-A2A-07). Title and body are agent text: untrusted.</summary>
public sealed record PendingQuestion(string Id, AgentId Agent, string Title, string Body, DateTimeOffset ExpiresAt);
