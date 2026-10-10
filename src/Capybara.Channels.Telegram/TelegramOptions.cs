namespace Capybara.Channels.Telegram;

/// <summary>Telegram bot configuration for fallback, content-free push (FR-CLI-03, FR-CLI-04).</summary>
public sealed record TelegramOptions(string BotToken, string ChatId);
