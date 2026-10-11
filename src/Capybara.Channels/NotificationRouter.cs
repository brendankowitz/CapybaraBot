using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels;

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
    private ChannelOptions _options = options;

    /// <summary>The settings routing uses now; replaced as a whole by <see cref="UpdateOptions"/> (FR-CLI-07).</summary>
    public ChannelOptions Options => Volatile.Read(ref _options);

    /// <summary>Applies new per-agent toggles and quiet hours to every notification routed from now on (FR-CLI-07).</summary>
    public void UpdateOptions(ChannelOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref _options, value);
    }

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
        // The batch timer is one-shot: release it so the next low-risk approval arms a fresh window.
        lock (_batchGate)
        {
            _batchTimer?.Dispose();
            _batchTimer = null;
        }

        List<Notification> items = [];
        while (_batchedLowRiskApprovals.TryDequeue(out var item))
        {
            items.Add(item);
        }

        if (items.Count == 0)
        {
            return;
        }

        // Fixed, content-free text: a batch summary must never reuse item titles, which name agents and tools.
        var title = items.Count == 1 ? "1 approval needs review" : $"{items.Count} approvals need review";
        var summary = new Notification(
            NotificationKind.Approval,
            null,
            title,
            "Open approvals in the PWA.",
            NotificationPriority.Normal,
            "/approvals",
            IsSensitive: items.Any(n => n.IsSensitive));
        await DispatchAsync(summary, cancellationToken);
    }

    private bool IsDisabled(Notification notification) =>
        notification.Agent is { } agent
        && Options.AgentNotificationToggles.TryGetValue(agent, out var enabled)
        && !enabled;

    private bool IsSuppressedByFocus(Notification notification) =>
        presence.IsAnyFocused()
        && notification.Kind != NotificationKind.Approval
        && notification.Priority != NotificationPriority.Critical;

    private bool IsSuppressedByQuietHours(Notification notification)
    {
        var current = Options;
        if (!current.QuietHours.Enabled)
        {
            return false;
        }

        if (current.CriticalBypassesQuietHours && notification.Priority == NotificationPriority.Critical)
        {
            return false;
        }

        if (current.ApprovalBypassesQuietHours && notification.Kind == NotificationKind.Approval)
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var starts = current.QuietHours.StartsAt;
        var ends = current.QuietHours.EndsAt;
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
                static state => _ = ((NotificationRouter)state!).FlushFromTimerAsync(),
                this,
                Options.LowRiskBatchWindow,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushFromTimerAsync()
    {
        try
        {
            await FlushBatchesAsync();
        }
        catch (Exception ex)
        {
            LogBatchFlushFailed(_logger, ex.Message, ex);
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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
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

    /// <summary>
    /// Reduces a sensitive notification for channels that cannot carry sensitive content. Action buttons are dropped too:
    /// they would let someone approve blind, and would put a single-use nonce on an untrusted server.
    /// </summary>
    private static Notification StripSensitive(Notification notification) =>
        notification.IsSensitive
            ? notification with { Title = $"{notification.Kind} notification", Body = string.Empty, Actions = [], IsSensitive = false }
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

    private static bool IsRelativePath(string value) =>
        value.Length > 0 && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal);

    [LoggerMessage(1, LogLevel.Debug, "Notification channel {ChannelName} failed: {Reason}")]
    private static partial void LogChannelFailed(ILogger logger, string channelName, string reason, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "Flushing batched approval notifications failed: {Reason}")]
    private static partial void LogBatchFlushFailed(ILogger logger, string reason, Exception exception);

    [GeneratedRegex("!\\[[^\\]]*\\]\\([^)]*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageMarkdownRegex();

    [GeneratedRegex("https?://\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
