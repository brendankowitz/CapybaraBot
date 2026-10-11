namespace Capybara.Policy.Managed;

/// <summary>
/// SDK-neutral form of the runtime's <c>ManagedSettings.Permissions</c> (FR-SAF-01 layer 1). The runtime enforces these
/// itself as defense in depth; the host gate still evaluates every call.
/// </summary>
public sealed record ManagedPermissionPlan(IReadOnlyList<string> Deny, IReadOnlyList<string> Ask);
