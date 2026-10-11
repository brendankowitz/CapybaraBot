using System.Text.Json;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Makes the kill switch survive a restart (FR-SAF-06): <see cref="Policy.Safety.KillSwitch"/> is in memory, but a
/// crash or reboot must never count as the desktop's release. Written first when the switch engages and removed only
/// after a release, so composition can re-engage the switch before any runtime, pump or routine starts.
/// </summary>
/// <remarks>
/// Fails closed: only a journal that provably does not exist (no file, no directory) reads as released. A journal that
/// cannot be read or parsed reads as engaged, and the reason is kept in <see cref="LoadFailure"/> for a Critical alert.
/// </remarks>
public sealed class KillSwitchJournal(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Why the last <see cref="Load"/> could not read the journal, or <see langword="null"/> when it could.</summary>
    public string? LoadFailure { get; private set; }

    /// <summary>The reason recorded at engagement, or <see langword="null"/> when the switch was released.</summary>
    public string? Load()
    {
        LoadFailure = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<Entry>(stream, Json)?.Reason ?? "restored after restart";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Never read as released: a corrupt, locked or unreadable journal keeps the fleet stopped.
            LoadFailure = ex.GetType().Name;
            return $"restored after restart (journal unreadable: {LoadFailure})";
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
