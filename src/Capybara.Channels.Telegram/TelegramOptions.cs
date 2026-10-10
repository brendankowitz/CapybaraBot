namespace Capybara.Channels.Telegram;

/// <summary>Telegram bot configuration for fallback, content-free push and one-user inbound chat (FR-CLI-03, FR-CLI-04).</summary>
public sealed record TelegramOptions(string BotToken, string ChatId)
{
    /// <summary>Telegram Bot API root.</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://api.telegram.org/");

    /// <summary>The single Telegram user id whose messages are accepted; everyone else is silently dropped (FR-CLI-04).</summary>
    public string? AllowedUserId { get; init; }

    /// <summary>Server-side long-poll duration for <c>getUpdates</c>.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait between retries after a failed poll.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one update must have been failing continuously before it is skipped as poison. Keeps a short inbox outage
    /// from dropping the user's command; the update must also have failed at least
    /// <see cref="TelegramPollingService.MaxDeliveryAttempts"/> times.
    /// </summary>
    public TimeSpan PoisonMessageWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Tailnet base URL of the PWA; when set the deep link in a message is absolute, otherwise the bare path is sent.</summary>
    public Uri? DeepLinkBaseUrl { get; init; }
}
