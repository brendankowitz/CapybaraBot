namespace Capybara.Contracts.Messaging;

/// <summary>A result passed by reference: a file in the project workspace, not a chat blob (FR-A2A-04).</summary>
public sealed record Artifact(string Name, string Uri, string MediaType = "text/markdown");
