namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>An incognito conversation the agent's CLI still holds (FR-MEM-12) and when it last changed.</summary>
public sealed record IncognitoSessionInfo(string ConversationId, DateTimeOffset LastActive);
