using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Hosting.Sandboxing;

/// <summary>Renders sandbox-runtime settings with per-agent egress and write allowlists (FR-SAF-07, FR-HST-03).</summary>
public sealed class SrtPolicyBuilder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };

    public static string Build(SrtPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return JsonSerializer.Serialize(policy, Options);
    }
}

/// <summary>Serializable <c>srt</c> policy settings for a single agent (FR-SAF-07).</summary>
public sealed record SrtPolicy(string AgentId, IReadOnlyList<string> AllowDomains, IReadOnlyList<string> WriteAllowlist, bool DenyByDefault = true);
