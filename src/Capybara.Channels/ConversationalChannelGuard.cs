using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Channels;

/// <summary>Silently drops conversational messages from anyone except one allowlisted user id per channel (FR-CLI-04).</summary>
public sealed partial class ConversationalChannelGuard(IReadOnlyDictionary<string, string> allowedUserIds, ILogger<ConversationalChannelGuard>? logger = null)
{
    private readonly ILogger<ConversationalChannelGuard> _logger = logger ?? NullLogger<ConversationalChannelGuard>.Instance;

    public bool IsAllowed(string channel, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var allowed = allowedUserIds.TryGetValue(channel, out var expected) && string.Equals(expected, userId, StringComparison.Ordinal);
        if (!allowed)
        {
            LogDropped(_logger, channel, userId);
        }

        return allowed;
    }

    [LoggerMessage(1, LogLevel.Debug, "Dropped unauthorized {Channel} message from {UserId}.")]
    private static partial void LogDropped(ILogger logger, string channel, string userId);
}
