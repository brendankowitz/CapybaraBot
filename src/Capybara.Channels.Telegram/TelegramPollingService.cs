using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels.Telegram;

/// <summary>
/// Long-polls Telegram <c>getUpdates</c> and feeds each update to <see cref="TelegramUpdateHandler"/>, which drops anyone
/// but the allowlisted user (FR-CLI-04). No webhook is needed, so the gateway exposes nothing to the internet.
/// </summary>
/// <remarks>
/// The offset advances past every update, including ones the handler rejects or fails on, so a poison update can never
/// wedge the loop. Delivery is at-least-once across restarts: Telegram only forgets an update after a later call confirms it.
/// </remarks>
public sealed partial class TelegramPollingService(
    HttpClient httpClient,
    TelegramOptions options,
    TelegramUpdateHandler handler,
    TimeProvider timeProvider,
    ILogger<TelegramPollingService>? logger = null) : BackgroundService
{
    private readonly ILogger<TelegramPollingService> _logger = logger ?? NullLogger<TelegramPollingService>.Instance;
    private long _offset;

    /// <summary>The next <c>offset</c> to request: one past the last update seen.</summary>
    public long Offset => Interlocked.Read(ref _offset);

    /// <summary>Performs one long poll and returns the number of updates received.</summary>
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

        var count = 0;
        foreach (var update in result.EnumerateArray())
        {
            count++;
            var updateId = update.GetProperty("update_id").GetInt64();
            try
            {
                await handler.HandleAsync(update, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUpdateFailed(_logger, updateId, ex);
            }

            Interlocked.Exchange(ref _offset, updateId + 1);
        }

        return count;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(failures, 10)), options.MaxRetryDelay.TotalSeconds));
                LogPollFailed(_logger, delay, ex);
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Telegram getUpdates failed; retrying in {Delay}.")]
    private static partial void LogPollFailed(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "Handling Telegram update {UpdateId} failed; skipping it.")]
    private static partial void LogUpdateFailed(ILogger logger, long updateId, Exception exception);
}
