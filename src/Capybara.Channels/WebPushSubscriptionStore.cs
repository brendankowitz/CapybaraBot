using System.Text.Json;

namespace Capybara.Channels;

/// <summary>
/// Web Push subscription store (FR-CLI-06). With a <c>path</c> every change is written through to a JSON file, so
/// subscriptions survive a gateway restart; without one the store is memory only.
/// </summary>
/// <remarks>
/// The file holds push endpoints and keys, so it is written with owner-only permissions on Unix and replaced atomically
/// (temp file then move). A corrupt file fails construction loudly rather than silently dropping every subscription.
/// All members are thread-safe.
/// </remarks>
public sealed class WebPushSubscriptionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, WebPushSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly string? _path;

    public WebPushSubscriptionStore(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var stored = JsonSerializer.Deserialize<List<WebPushSubscription>>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException($"Web push subscription file '{path}' is empty or not a JSON array.");
        foreach (var subscription in stored)
        {
            _subscriptions[subscription.DeviceId] = subscription;
        }
    }

    public IReadOnlyList<WebPushSubscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions.Values];
            }
        }
    }

    /// <summary>Adds or replaces the subscription for the device and persists the change.</summary>
    public void Upsert(WebPushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (_gate)
        {
            _subscriptions[subscription.DeviceId] = subscription;
            Persist();
        }
    }

    /// <summary>Removes the device's subscription; returns whether one existed.</summary>
    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(deviceId))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, _subscriptions.Values, JsonOptions);
        }

        MoveWithRetry(temp, _path);
    }

    // On Windows, replacing a file that a scanner/indexer has just opened can fail transiently with "access denied";
    // a short bounded retry rides that out without hiding a persistent failure (the last attempt's exception propagates).
    private static void MoveWithRetry(string source, string destination)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
