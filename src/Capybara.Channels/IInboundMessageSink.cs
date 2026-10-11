namespace Capybara.Channels;

/// <summary>Receives accepted conversational-channel text after allowlist checks (FR-CLI-04).</summary>
public interface IInboundMessageSink
{
    /// <summary>
    /// Delivers accepted text to the inbox. Delivery is at-least-once: <paramref name="messageId"/> is a stable idempotency
    /// key (e.g. <c>telegram:{update_id}</c>) that is identical on every redelivery, so the inbox must dedupe on it.
    /// Throwing signals failure and makes the adapter redeliver later.
    /// </summary>
    ValueTask ReceiveAsync(string channel, string userId, string text, string messageId, CancellationToken cancellationToken = default);
}
