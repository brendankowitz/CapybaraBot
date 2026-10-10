using System.Net.Http.Json;
using System.Text.Json;
using Capybara.Observability.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels.Telegram;

/// <summary>
/// Long-polls Telegram <c>getUpdates</c> and feeds each update to <see cref="TelegramUpdateHandler"/>, which drops anyone
/// but the allowlisted user (FR-CLI-04). No webhook is needed, so the gateway exposes nothing to the internet.
/// </summary>
/// <remarks>
/// The offset only advances past an update once the handler has accepted it (or it was dropped by the allowlist), so a
/// failing inbox sink gets the same message again on the next poll: no inbox message is lost (NFR-02). An update is skipped
/// as poison only after it has failed continuously for <see cref="TelegramOptions.PoisonMessageWindow"/> and at least
/// <see cref="MaxDeliveryAttempts"/> times, and an alert is raised; so a brief inbox outage loses nothing, yet one bad
/// update cannot wedge the loop forever. Delivery is at-least-once; the sink dedupes on the message id.
/// Only a real shutdown stops the loop: transport timeouts (also <see cref="OperationCanceledException"/>) back off and retry.
/// </remarks>
public sealed partial class TelegramPollingService(
    HttpClient httpClient,
    TelegramOptions options,
    TelegramUpdateHandler handler,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService>? logger = null,
    IAlertSink? alerts = null) : BackgroundService
{
    /// <summary>Minimum failed deliveries of one update before it can be skipped as poison.</summary>
    public const int MaxDeliveryAttempts = 5;

    private readonly ILogger<TelegramPollingService> _logger = logger ?? NullLogger<TelegramPollingService>.Instance;
    private long _offset;
    private long _failingUpdateId = -1;
    private int _failedAttempts;
    private DateTimeOffset _failingSince;

    /// <summary>The next <c>offset</c> to request: one past the last update fully handled.</summary>
    public long Offset => Interlocked.Read(ref _offset);

    /// <summary>
    /// Performs one long poll and returns the number of updates handled. Rethrows the sink's exception for an update that
    /// failed but is not yet poison; the offset stays on that update so it is redelivered.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var request = new
        {
            offset = Offset,
            timeout = (int)options.PollTimeout.TotalSeconds,
            allowed_updates = new[] { "message" },
        };
        using var response = await httpClient.PostAsJsonAsync($"bot{options.BotToken}/getUpdates", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!body.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Telegram getUpdates response has no result array.");
        }

        var handled = 0;
        foreach (var update in result.EnumerateArray())
        {
            var updateId = update.GetProperty("update_id").GetInt64();
            try
            {
                await handler.HandleAsync(update, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var now = timeProvider.GetUtcNow();
                if (updateId == _failingUpdateId)
                {
                    _failedAttempts++;
                }
                else
                {
                    _failingUpdateId = updateId;
                    _failedAttempts = 1;
                    _failingSince = now;
                }

                if (_failedAttempts < MaxDeliveryAttempts || now - _failingSince < options.PoisonMessageWindow)
                {
                    LogUpdateFailed(_logger, updateId, _failedAttempts, now - _failingSince, ex);
                    throw;
                }

                LogUpdateSkipped(_logger, updateId, _failedAttempts, ex);
                await TryPublishSkippedAlertAsync(updateId, cancellationToken);
            }

            Interlocked.Exchange(ref _offset, updateId + 1);
            handled++;
        }

        return handled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                failures = 0;
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                // Includes TaskCanceledException from an HttpClient timeout: the poller must survive those.
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(failures, 10)), options.MaxRetryDelay.TotalSeconds));
                LogPollFailed(_logger, delay, ex);
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task TryPublishSkippedAlertAsync(long updateId, CancellationToken cancellationToken)
    {
        if (alerts is null)
        {
            return;
        }

        try
        {
            await alerts.PublishAsync(new Alert(
                "telegram.inbound_skipped",
                AlertSeverity.Warning,
                $"Telegram update {updateId} was skipped after failing for {options.PoisonMessageWindow} ({_failedAttempts} attempts).",
                timeProvider.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A broken alert sink must not keep the poison update (and everything behind it) stuck.
            LogAlertFailed(_logger, updateId, ex);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Telegram getUpdates failed; retrying in {Delay}.")]
    private static partial void LogPollFailed(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(2, LogLevel.Warning, "Handling Telegram update {UpdateId} failed (attempt {Attempt}, failing for {FailingFor}); it will be redelivered.")]
    private static partial void LogUpdateFailed(ILogger logger, long updateId, int attempt, TimeSpan failingFor, Exception exception);

    [LoggerMessage(3, LogLevel.Error, "Telegram update {UpdateId} failed {Attempts} times over the poison window; skipping it.")]
    private static partial void LogUpdateSkipped(ILogger logger, long updateId, int attempts, Exception exception);

    [LoggerMessage(4, LogLevel.Error, "Publishing the skipped-update alert for Telegram update {UpdateId} failed.")]
    private static partial void LogAlertFailed(ILogger logger, long updateId, Exception exception);
}
