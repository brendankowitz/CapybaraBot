using System.Collections.Concurrent;

namespace Fleet.Channels;

/// <summary>In-memory Web Push subscription store; gateway persistence can wrap this contract later (FR-CLI-06).</summary>
public sealed class WebPushSubscriptionStore
{
    private readonly ConcurrentDictionary<string, WebPushSubscription> _subscriptions = new(StringComparer.Ordinal);

    public IReadOnlyList<WebPushSubscription> Subscriptions => [.. _subscriptions.Values];

    public void Upsert(WebPushSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _subscriptions[subscription.DeviceId] = subscription;
    }

    public bool Remove(string deviceId) => _subscriptions.TryRemove(deviceId, out _);
}
