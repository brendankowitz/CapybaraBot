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
/// <para>
/// Delivery is at-least-once (NFR-02): the offset only advances past an update once the handler has accepted it (or the
/// allowlist dropped it), so a failing inbox sink gets the same update, with the same <c>telegram:{update_id}</c> message
/// id for dedupe, on the next poll.
/// </para>
/// <para>
/// One exception keeps a bad update from wedging the poller forever: it is skipped as poison, with a content-free alert,
/// once it has failed at least <see cref="MaxDeliveryAttempts"/> handler attempts and its failing time has reached
/// <see cref="TelegramOptions.PoisonMessageWindow"/>. Failing time runs from the start of its first failed attempt and is
/// paused from the start of a failed <c>getUpdates</c> until the next attempt, so a network or Telegram outage neither
/// counts toward the window nor shortcuts it, while intermittent poll failures only delay the skip, never prevent it.
/// </para>
/// <para>
/// Only a real shutdown (the stopping token) ends the loop; every other error, including transport timeouts surfacing as
/// <see cref="OperationCanceledException"/>, backs off exponentially up to <see cref="TelegramOptions.MaxRetryDelay"/>.
/// <see cref="PollOnceAsync"/> keeps per-instance retry state and must not run concurrently with itself or the hosted loop.
/// </para>
/// </remarks>
public sealed partial class TelegramPollingService(
    HttpClient httpClient,
    TelegramOptions options,
    TelegramUpdateHandler handler,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService>? logger = null,
    IAlertSink? alerts = null) : BackgroundService
{
    /// <summary>Minimum failed handler attempts of one update before it can be skipped as poison.</summary>
    public const int MaxDeliveryAttempts = 5;

    private readonly ILogger<TelegramPollingService> _logger = logger ?? NullLogger<TelegramPollingService>.Instance;
    private long _offset;

    // The update at the offset that is currently failing, if any; only one can be, as the batch stops at the first failure.
    private FailureStreak? _streak;

    /// <summary>The next <c>offset</c> to request: one past the last update fully handled.</summary>
    public long Offset => Interlocked.Read(ref _offset);

    /// <summary>
    /// Performs one long poll and returns the number of updates the offset advanced past. Rethrows the sink's exception for
    /// an update that failed but is not yet poison; the offset stays on that update so it is redelivered.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The response is malformed, or it made no progress because every update in it lacks a usable <c>update_id</c>.
    /// </exception>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var pollStart = timeProvider.GetTimestamp();
        JsonDocument body;
        try
        {
            body = await FetchUpdatesAsync(cancellationToken);
        }
        catch
        {
            _streak?.Pause(pollStart);
            throw;
        }

        using (body)
        {
            return await HandleBatchAsync(body.RootElement.GetProperty("result"), cancellationToken);
        }
    }

    private async Task<JsonDocument> FetchUpdatesAsync(CancellationToken cancellationToken)
    {
        var request = new
        {
            offset = Offset,
            timeout = (int)options.PollTimeout.TotalSeconds,
            allowed_updates = new[] { "message" },
        };
        using var response = await httpClient.PostAsJsonAsync($"bot{options.BotToken}/getUpdates", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            body.Dispose();
            throw new InvalidDataException("Telegram getUpdates response has no result array.");
        }

        return body;
    }

    private async Task<int> HandleBatchAsync(JsonElement updates, CancellationToken cancellationToken)
    {
        var handled = 0;
        var unidentifiable = 0;
        foreach (var update in updates.EnumerateArray())
        {
            // Without an update_id an update cannot be acknowledged or tracked, so it can't follow the retry path. Skip it:
            // a later update's offset confirms it, and if nothing follows it the batch fails below (with backoff).
            if (!TelegramUpdateHandler.TryGetUpdateId(update, out var updateId))
            {
                unidentifiable++;
                LogUpdateUnidentifiable(_logger);
                continue;
            }

            var attemptStart = timeProvider.GetTimestamp();
            try
            {
                await handler.HandleAsync(update, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var streak = RecordFailedAttempt(updateId, attemptStart);
                if (streak.Attempts < MaxDeliveryAttempts || streak.FailingFor < options.PoisonMessageWindow)
                {
                    LogUpdateFailed(_logger, updateId, streak.Attempts, streak.FailingFor, ex);
                    throw;
                }

                LogUpdateSkipped(_logger, updateId, streak.Attempts, streak.FailingFor, ex);
                await TryPublishSkippedAlertAsync(streak, cancellationToken);
            }

            _streak = null;
            Interlocked.Exchange(ref _offset, updateId + 1);
            handled++;
        }

        if (handled == 0 && unidentifiable > 0)
        {
            // Telegram re-sends the same unusable update at once; failing here turns a hot loop into a backoff.
            throw new InvalidDataException($"Telegram getUpdates returned {unidentifiable} update(s) without a usable update_id.");
        }

        return handled;
    }

    private FailureStreak RecordFailedAttempt(long updateId, long attemptStart)
    {
        if (_streak?.UpdateId == updateId)
        {
            _streak.RecordRetry(attemptStart);
        }
        else
        {
            _streak = new FailureStreak(updateId, attemptStart, timeProvider);
        }

        return _streak;
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

    private async Task TryPublishSkippedAlertAsync(FailureStreak streak, CancellationToken cancellationToken)
    {
        if (alerts is null)
        {
            return;
        }

        try
        {
            // Content-free: the update id and counts identify the loss without echoing what the user wrote.
            await alerts.PublishAsync(new Alert(
                "telegram.inbound_skipped",
                AlertSeverity.Warning,
                $"Telegram update {streak.UpdateId} was skipped after failing for {streak.FailingFor:c} ({streak.Attempts} attempts).",
                timeProvider.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A broken alert sink must not keep the poison update (and everything behind it) stuck.
            LogAlertFailed(_logger, streak.UpdateId, ex);
        }
    }

    /// <summary>
    /// Failed handler attempts of one update and how long it has been failing. Uses the monotonic timestamp so wall-clock
    /// jumps can't move the window. Failing time accrues between attempt starts and stops at the start of a failed
    /// <c>getUpdates</c> (<see cref="Pause"/>); the next attempt resumes it without counting the outage.
    /// </summary>
    private sealed class FailureStreak(long updateId, long firstAttemptStart, TimeProvider time)
    {
        private long _accruingSince = firstAttemptStart;
        private bool _paused;

        public long UpdateId => updateId;

        public int Attempts { get; private set; } = 1;

        public TimeSpan FailingFor { get; private set; }

        public void RecordRetry(long attemptStart)
        {
            if (!_paused)
            {
                FailingFor += time.GetElapsedTime(_accruingSince, attemptStart);
            }

            _paused = false;
            _accruingSince = attemptStart;
            Attempts++;
        }

        public void Pause(long pollStart)
        {
            if (_paused)
            {
                return;
            }

            FailingFor += time.GetElapsedTime(_accruingSince, pollStart);
            _paused = true;
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Telegram getUpdates failed; retrying in {Delay}.")]
    private static partial void LogPollFailed(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(2, LogLevel.Warning, "Handling Telegram update {UpdateId} failed (attempt {Attempt}, failing for {FailingFor}); it will be redelivered.")]
    private static partial void LogUpdateFailed(ILogger logger, long updateId, int attempt, TimeSpan failingFor, Exception exception);

    [LoggerMessage(3, LogLevel.Error, "Telegram update {UpdateId} failed {Attempts} times over {FailingFor}; skipping it as poison.")]
    private static partial void LogUpdateSkipped(ILogger logger, long updateId, int attempts, TimeSpan failingFor, Exception exception);

    [LoggerMessage(4, LogLevel.Error, "Publishing the skipped-update alert for Telegram update {UpdateId} failed.")]
    private static partial void LogAlertFailed(ILogger logger, long updateId, Exception exception);

    [LoggerMessage(5, LogLevel.Error, "Telegram returned an update without a usable update_id; it cannot be tracked and is skipped.")]
    private static partial void LogUpdateUnidentifiable(ILogger logger);
}
