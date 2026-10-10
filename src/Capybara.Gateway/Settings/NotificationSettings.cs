using System.Text.Json;
using Capybara.Channels;
using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Orchestration.Registry;

namespace Capybara.Gateway.Settings;

/// <summary>
/// The user's notification choices from the PWA (FR-CLI-07): per-agent toggles and quiet hours. Saved to
/// <c>{state}/channels/notification-settings.json</c>; once saved they override <c>agent.yaml</c>'s
/// <c>notifications</c> and the configured quiet hours, and take effect on the router at once.
/// </summary>
/// <remarks>Thread-safe; saves are serialized and the file is replaced atomically.</remarks>
public sealed class NotificationSettings(string path, NotificationRouter router, AgentRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>Applies previously saved settings to the options the router starts with.</summary>
    /// <exception cref="InvalidDataException">The settings file exists but is not readable as settings.</exception>
    public static ChannelOptions ApplySaved(ChannelOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(path))
        {
            return options;
        }

        var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException($"Notification settings file '{path}' is empty.");
        var toggles = new Dictionary<AgentId, bool>(options.AgentNotificationToggles);
        foreach (var (agent, enabled) in saved.Agents)
        {
            if (AgentId.TryParse(agent, out var id))
            {
                toggles[id] = enabled;
            }
        }

        return options with { AgentNotificationToggles = toggles, QuietHours = saved.QuietHours };
    }

    /// <summary>Every agent's toggle (enabled unless switched off) and the quiet hours in effect.</summary>
    public NotificationSettingsDto Get()
    {
        var options = router.Options;
        return new NotificationSettingsDto(
            [.. registry.All
                .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
                .Select(a => new AgentNotificationToggleDto(a.Id.Value, options.AgentNotificationToggles.GetValueOrDefault(a.Id, true)))],
            new QuietHoursDto(options.QuietHours.Enabled, Format(options.QuietHours.StartsAt), Format(options.QuietHours.EndsAt)));
    }

    /// <summary>Saves and applies new settings; agents not mentioned keep their toggle.</summary>
    public void Save(IReadOnlyDictionary<AgentId, bool> toggles, QuietHoursOptions quietHours)
    {
        ArgumentNullException.ThrowIfNull(toggles);
        ArgumentNullException.ThrowIfNull(quietHours);
        lock (_gate)
        {
            var merged = new Dictionary<AgentId, bool>(router.Options.AgentNotificationToggles);
            foreach (var (agent, enabled) in toggles)
            {
                merged[agent] = enabled;
            }

            var saved = new Saved(merged.ToDictionary(kv => kv.Key.Value, kv => kv.Value, StringComparer.Ordinal), quietHours);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(saved, Json));
            File.Move(temp, path, overwrite: true);
            router.UpdateOptions(router.Options with { AgentNotificationToggles = merged, QuietHours = quietHours });
        }
    }

    /// <summary>Parses <c>HH:mm</c> (24-hour, exactly as the clients send it).</summary>
    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);

    private static string Format(TimeOnly time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Saved(Dictionary<string, bool> Agents, QuietHoursOptions QuietHours);
}
