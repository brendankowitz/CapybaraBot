namespace Capybara.Gateway.Configuration;

/// <summary><c>Capybara:Channels</c>: push and conversational channels (FR-CLI-03, FR-CLI-04, FR-CLI-06, FR-CLI-07).</summary>
public sealed class ChannelHostOptions
{
    public const string SectionName = "Capybara:Channels";

    public NtfySettings Ntfy { get; set; } = new();

    public TelegramSettings Telegram { get; set; } = new();

    public WebPushSettings WebPush { get; set; } = new();

    public QuietHoursSettings QuietHours { get; set; } = new();
}

/// <summary>Self-hosted ntfy, the preferred push channel. Enabled when <see cref="BaseUrl"/> and <see cref="Topic"/> are set.</summary>
public sealed class NtfySettings
{
    public Uri? BaseUrl { get; set; }

    public string? Topic { get; set; }

    /// <summary>systemd credential holding the ntfy access token, if the topic is protected.</summary>
    public string? AccessTokenCredential { get; set; }

    /// <summary>
    /// Whether agent and tool names may be sent. True by default because the supported deployment is a self-hosted ntfy
    /// reachable only over the tailnet; set false for public ntfy.sh.
    /// </summary>
    public bool SupportsSensitiveContent { get; set; } = true;

    /// <summary>Tailnet URL of the PWA, for click targets and action buttons (ntfy needs absolute URLs).</summary>
    public Uri? GatewayBaseUrl { get; set; }
}

/// <summary>Telegram fallback channel. Enabled when the bot token credential is readable and <see cref="ChatId"/> is set.</summary>
public sealed class TelegramSettings
{
    public string BotTokenCredential { get; set; } = "telegram-bot-token";

    public string? ChatId { get; set; }

    /// <summary>The only Telegram user whose DMs are accepted (FR-CLI-04). Unset: notifications only, no polling.</summary>
    public string? AllowedUserId { get; set; }

    public Uri? DeepLinkBaseUrl { get; set; }
}

/// <summary>PWA Web Push (FR-CLI-06). Enabled when <see cref="PublicKey"/> and the private key credential are set.</summary>
public sealed class WebPushSettings
{
    /// <summary>VAPID subject, e.g. <c>mailto:owner@example.com</c>.</summary>
    public string Subject { get; set; } = "mailto:capybara@localhost";

    public string? PublicKey { get; set; }

    public string PrivateKeyCredential { get; set; } = "webpush-vapid-private-key";
}

public sealed class QuietHoursSettings
{
    public bool Enabled { get; set; }

    public TimeOnly StartsAt { get; set; } = new(22, 0);

    public TimeOnly EndsAt { get; set; } = new(7, 0);
}
