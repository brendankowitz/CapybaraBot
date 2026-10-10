namespace Capybara.Channels;

/// <summary>Tracks focused PWA devices so push can be suppressed while a user is already present (FR-CLI-07).</summary>
public interface IPresenceTracker
{
    void SetFocused(string deviceId, bool focused);

    bool IsAnyFocused();
}
