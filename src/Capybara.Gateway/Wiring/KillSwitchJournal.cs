using System.Text.Json;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Makes the kill switch survive a restart (FR-SAF-06): <see cref="Policy.Safety.KillSwitch"/> is in memory, but a
/// crash or reboot must never count as the desktop's release. Written first when the switch engages and removed only
/// after a release, so composition can re-engage the switch before any runtime, pump or routine starts.
/// </summary>
public sealed class KillSwitchJournal(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The reason recorded at engagement, or <see langword="null"/> when the switch was released.</summary>
    /// <remarks>An unreadable journal counts as engaged: when in doubt, stay stopped.</remarks>
    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json)?.Reason ?? "restored after restart";
        }
        catch (JsonException)
        {
            return "restored after restart (unreadable journal)";
        }
    }

    public void SaveEngaged(string reason, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new Entry(reason, at), Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Clear() => File.Delete(path);

    private sealed record Entry(string Reason, DateTimeOffset At);
}
