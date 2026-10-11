using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Hosting.Sandboxing;

/// <summary>
/// Renders sandbox-runtime (<c>srt</c>) settings with per-agent egress and write allowlists (FR-SAF-07, FR-HST-03).
/// Uses the settings schema <c>srt --settings</c> reads. Network and writes are allow-only (an empty <c>allowedDomains</c> or
/// <c>allowWrite</c> means none). Reads are the opposite: allowed everywhere unless listed in <c>denyRead</c>, and <c>allowRead</c>
/// re-opens paths inside a denied region (it wins over <c>denyRead</c>, whereas <c>denyWrite</c> wins over <c>allowWrite</c>).
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
                new SrtFilesystem(policy.DenyRead ?? [], policy.AllowRead ?? [], policy.WriteAllowlist, policy.DenyWrite ?? [])),
            Options);
    }

    private sealed record SrtSettings(
        [property: JsonPropertyName("network")] SrtNetwork Network,
        [property: JsonPropertyName("filesystem")] SrtFilesystem Filesystem);

    private sealed record SrtNetwork(IReadOnlyList<string> AllowedDomains, IReadOnlyList<string> DeniedDomains);

    private sealed record SrtFilesystem(IReadOnlyList<string> DenyRead, IReadOnlyList<string> AllowRead, IReadOnlyList<string> AllowWrite, IReadOnlyList<string> DenyWrite);
}

/// <summary>
/// Per-agent <c>srt</c> policy (FR-SAF-07): the domains the agent may reach, the paths it may write, and the read
/// denials (with re-allowed exceptions) that isolate it from other agents' data and secrets.
/// </summary>
public sealed record SrtPolicy(
    string AgentId,
    IReadOnlyList<string> AllowDomains,
    IReadOnlyList<string> WriteAllowlist,
    IReadOnlyList<string>? DenyRead = null,
    IReadOnlyList<string>? AllowRead = null,
    IReadOnlyList<string>? DenyWrite = null);
