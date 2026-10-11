namespace Capybara.Channels;

/// <summary>Nonce-bound notification action whose callback path stays on the tailnet gateway (FR-CLI-03).</summary>
public sealed record NotificationAction(string Label, string CallbackPath);
