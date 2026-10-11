using Capybara.Contracts.Messaging;

namespace Capybara.Memory.Abstractions;

/// <summary>Turn text offered to conversational memory providers without adding any external dependency (FR-MEM-11).</summary>
public sealed record MemoryIngestItem(string Text, string Agent, string SessionId, Taint Taint, DateTimeOffset Timestamp);
