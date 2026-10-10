namespace Capybara.Channels;

/// <summary>Receives accepted conversational-channel text after allowlist checks (FR-CLI-04).</summary>
public interface IInboundMessageSink
{
    ValueTask ReceiveAsync(string channel, string userId, string text, CancellationToken cancellationToken = default);
}
