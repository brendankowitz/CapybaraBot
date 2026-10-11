namespace Capybara.Observability.Alerts;

/// <summary>A user-visible alert (FR-OBS-05). Payloads stay content-free so they are safe for push channels.</summary>
public sealed record Alert(string Kind, AlertSeverity Severity, string Message, DateTimeOffset At, string? Agent = null);
