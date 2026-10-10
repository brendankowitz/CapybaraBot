using System.Globalization;
using System.Text.RegularExpressions;

namespace Capybara.Hosting.Agents;

/// <summary>
/// Validation for every value interpolated into a systemd unit, drop-in or Quadlet file (FR-HST-02). A value containing a
/// control character (newline included) could start a new directive, so it is rejected rather than escaped.
/// </summary>
internal static partial class UnitFileValue
{
    /// <summary>A single-line value; <paramref name="allowWhitespace"/> is false where the format splits on spaces.</summary>
    public static string Text(string? value, string name, bool allowWhitespace = true, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(value) && !allowEmpty)
        {
            throw new ArgumentException($"{name} is required.", name);
        }

        value ??= string.Empty;
        if (value.Any(char.IsControl) || (!allowWhitespace && value.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException($"{name} contains characters that are not allowed in a unit file.", name);
        }

        return value;
    }

    /// <summary>A systemd <c>CPUQuota=</c> percentage such as <c>50%</c>.</summary>
    public static string CpuQuota(string value) =>
        CpuQuotaPattern().IsMatch(value)
            ? value
            : throw new ArgumentException("CpuQuota must be a percentage such as '50%'.", nameof(value));

    public static string PositiveBytes(long value) =>
        value > 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("MemoryMaxBytes must be positive.", nameof(value));

    /// <summary>An environment variable name usable in <c>Environment=NAME=value</c>.</summary>
    public static string EnvironmentName(string name) =>
        name is not null && EnvironmentNamePattern().IsMatch(name)
            ? name
            : throw new ArgumentException($"'{name}' is not a valid environment variable name.", nameof(name));

    /// <summary>
    /// The canonical text of an IP address or CIDR, or null when <paramref name="value"/> is anything else (a domain, a
    /// wildcard). Canonical output is re-rendered from the parsed value, never copied from the input.
    /// </summary>
    public static string? IpOrCidr(string value)
    {
        if (value.Any(char.IsControl) || value.Contains('%'))
        {
            throw new ArgumentException("An egress entry contains control characters or an IPv6 scope id.", nameof(value));
        }

        if (System.Net.IPNetwork.TryParse(value, out var network))
        {
            return network.ToString();
        }

        // IPAddress.TryParse also accepts bare numbers ("1" is 0.0.0.1); only dotted or colon notation is an address here.
        return (value.Contains('.') || value.Contains(':')) && System.Net.IPAddress.TryParse(value, out var address) ? address.ToString() : null;
    }

    [GeneratedRegex(@"^\d{1,5}(\.\d+)?%$", RegexOptions.CultureInvariant)]
    private static partial Regex CpuQuotaPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();
}
