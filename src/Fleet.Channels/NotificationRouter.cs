using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Fleet.Observability.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Channels;

/// <summary>Routes safe, minimal push notifications with toggles, quiet hours, focus suppression, fallback, and batching (FR-CLI-03, FR-CLI-07, FR-SAF-10, NFR-04).</summary>
public sealed partial class NotificationRouter(
    IEnumerable<INotificationChannel> channels,
    IPresenceTracker presence,
    IAlertSink alerts,
    ChannelOptions options,
    TimeProvider timeProvider,
    ILogger<NotificationRouter>? logger = null)
{
    private readonly ILogger<NotificationRouter> _logger = logger ?? NullLogger<NotificationRouter>.Instance;
    private readonly IReadOnlyList<INotificationChannel> _channels = [.. channels];
    private readonly ConcurrentQueue<Notification> _batchedLowRiskApprovals = new();
    private readonly object _batchGate = new();
    private ITimer? _batchTimer;

    public async ValueTask RouteAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (IsDisabled(notification) || IsSuppressedByFocus(notification) || IsSuppressedByQuietHours(notification))
        {
            return;
        }

        if (ShouldBatch(notification))
        {
            EnqueueBatch(notification);
            return;
        }

        await DispatchAsync(notification, cancellationToken);
    }

    public async ValueTask FlushBatchesAsync(CancellationToken cancellationToken = default)
    {
        List<Notification> items = [];
        while (_batchedLowRiskApprovals.TryDequeue(out var item))
        {
            items.Add(item);
        }

        if (items.Count == 0)
        {
            return;
        }

        var first = items[0];
        var agents = string.Join(", ", items.Select(n => n.Agent?.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal));
        var title = items.Count == 1 ? first.Title : $"{items.Count} low-risk approvals need review";
        var body = string.IsNullOrWhiteSpace(agents) ? "Open approvals in the PWA." : $"Agents: {agents}. Open approvals in the PWA.";
        var summary = new Notification(NotificationKind.Approval, first.Agent, title, body, NotificationPriority.Normal, "/approvals", IsSensitive: false);
        await DispatchAsync(summary, cancellationToken);
    }

    private bool IsDisabled(Notification notification) =>
        notification.Agent is { } agent
        && options.AgentNotificationToggles.TryGetValue(agent, out var enabled)
        && !enabled;

    private bool IsSuppressedByFocus(Notification notification) =>
        presence.IsAnyFocused()
        && notification.Kind != NotificationKind.Approval
        && notification.Priority != NotificationPriority.Critical;

    private bool IsSuppressedByQuietHours(Notification notification)
    {
        if (!options.QuietHours.Enabled)
        {
            return false;
        }

        if (options.CriticalBypassesQuietHours && notification.Priority == NotificationPriority.Critical)
        {
            return false;
        }

        if (options.ApprovalBypassesQuietHours && notification.Kind == NotificationKind.Approval)
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var starts = options.QuietHours.StartsAt;
        var ends = options.QuietHours.EndsAt;
        return starts <= ends ? now >= starts && now < ends : now >= starts || now < ends;
    }

    private static bool ShouldBatch(Notification notification) =>
        notification.Kind == NotificationKind.Approval && notification.Priority == NotificationPriority.Low;

    private void EnqueueBatch(Notification notification)
    {
        _batchedLowRiskApprovals.Enqueue(notification);
        lock (_batchGate)
        {
            _batchTimer ??= timeProvider.CreateTimer(
                static state => _ = ((NotificationRouter)state!).FlushBatchesAsync().AsTask(),
                this,
                options.LowRiskBatchWindow,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async ValueTask DispatchAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (_channels.Count == 0)
        {
            await PublishFailureAlertAsync(notification, cancellationToken);
            return;
        }

        foreach (var channel in _channels)
        {
            var outbound = channel.SupportsSensitiveContent ? Sanitize(notification) : StripSensitive(Sanitize(notification));
            try
            {
                await channel.SendAsync(outbound, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogChannelFailed(_logger, channel.Name, ex.Message, ex);
            }
        }

        await PublishFailureAlertAsync(notification, cancellationToken);
    }

    private async ValueTask PublishFailureAlertAsync(Notification notification, CancellationToken cancellationToken)
    {
        await alerts.PublishAsync(new Alert(
            "notification.delivery_failed",
            AlertSeverity.Warning,
            $"All notification channels failed for {notification.Kind}.",
            timeProvider.GetUtcNow(),
            notification.Agent?.Value), cancellationToken);
    }

    private static Notification StripSensitive(Notification notification) =>
        notification.IsSensitive
            ? notification with { Title = $"{notification.Kind} notification", Body = string.Empty, IsSensitive = false }
            : notification;

    private static Notification Sanitize(Notification notification)
    {
        var deepLink = IsRelativePath(notification.DeepLinkPath) ? notification.DeepLinkPath : "/";
        return notification with
        {
            Title = RemoveUnsafeText(notification.Title),
            Body = RemoveUnsafeText(notification.Body),
            DeepLinkPath = deepLink,
            Actions = [.. notification.Actions
                .Where(a => IsRelativePath(a.CallbackPath))
                .Select(a => a with { Label = RemoveUnsafeText(a.Label) })],
        };
    }

    private static string RemoveUnsafeText(string value) => UrlRegex().Replace(ImageMarkdownRegex().Replace(value, "[image]"), "[link]");

    private static bool IsRelativePath(string value) => value.Length > 0 && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal);

    [LoggerMessage(1, LogLevel.Debug, "Notification channel {ChannelName} failed: {Reason}")]
    private static partial void LogChannelFailed(ILogger logger, string channelName, string reason, Exception exception);

    [GeneratedRegex("!\\[[^\\]]*\\]\\([^)]*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageMarkdownRegex();

    [GeneratedRegex("https?://\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
