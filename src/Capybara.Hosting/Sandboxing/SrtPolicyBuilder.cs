using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Hosting.Sandboxing;

/// <summary>
/// Renders sandbox-runtime (<c>srt</c>) settings with per-agent egress and write allowlists (FR-SAF-07, FR-HST-03).
/// Uses the settings schema <c>srt --settings</c> reads: <c>network.allowedDomains</c> and <c>filesystem.allowWrite</c>.
/// srt denies all network and all writes outside the allow lists, so an empty list means "none".
/// </summary>
public sealed class SrtPolicyBuilder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Build(SrtPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return JsonSerializer.Serialize(
            new SrtSettings(
                new SrtNetwork(policy.AllowDomains, DeniedDomains: []),
                new SrtFilesystem(DenyRead: [], policy.WriteAllowlist, DenyWrite: [])),
            Options);
    }

    private sealed record SrtSettings(
        [property: JsonPropertyName("network")] SrtNetwork Network,
        [property: JsonPropertyName("filesystem")] SrtFilesystem Filesystem);

    private sealed record SrtNetwork(IReadOnlyList<string> AllowedDomains, IReadOnlyList<string> DeniedDomains);

    private sealed record SrtFilesystem(IReadOnlyList<string> DenyRead, IReadOnlyList<string> AllowWrite, IReadOnlyList<string> DenyWrite);
}

/// <summary>Per-agent <c>srt</c> policy: the domains the agent may reach and the paths it may write (FR-SAF-07).</summary>
public sealed record SrtPolicy(string AgentId, IReadOnlyList<string> AllowDomains, IReadOnlyList<string> WriteAllowlist);
