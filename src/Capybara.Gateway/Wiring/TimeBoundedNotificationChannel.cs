using Capybara.Channels;

namespace Capybara.Gateway.Wiring;

/// <summary>
/// Bounds one notification channel's send (NFR-04: an approval push within 10 s). A send that runs out of time throws
/// <see cref="TimeoutException"/>, which the router treats like any channel failure and falls back to the next channel,
/// instead of a hung endpoint holding the push for the HTTP client's default 100 s.
/// </summary>
/// <remarks>
/// Applied by composition to every registered channel, including Telegram, whose HTTP client keeps a long timeout for
/// its long-poll and so cannot be shortened at the client.
/// </remarks>
public sealed class TimeBoundedNotificationChannel(INotificationChannel inner, TimeSpan timeout, TimeProvider time) : INotificationChannel
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public string Name => inner.Name;

    public bool SupportsSensitiveContent => inner.SupportsSensitiveContent;

    public async ValueTask SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await inner.SendAsync(notification, linked.Token);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Notification channel '{inner.Name}' did not answer within {timeout.TotalSeconds:0} s.", ex);
        }
    }
}
