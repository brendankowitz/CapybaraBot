using System.Collections.Concurrent;
using Capybara.Connections.OAuth;

namespace Capybara.Gateway.ConnectionControl;

/// <summary>
/// Consent requests the broker routed to the user (FR-CON-08), by connection, until they expire. The authorization link
/// is shown only on the authenticated PWA's connections page, never in a push payload.
/// </summary>
public sealed class PendingConsents(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, InteractiveAuthRequest> _requests = new(StringComparer.Ordinal);

    /// <summary>Keeps the request; a newer one for the same connection replaces it.</summary>
    public void Add(InteractiveAuthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests[request.Connection] = request;
    }

    /// <summary>The connection's open request, or <see langword="null"/> when there is none or it expired.</summary>
    public InteractiveAuthRequest? Find(string connection)
    {
        if (!_requests.TryGetValue(connection, out var request))
        {
            return null;
        }

        if (request.ExpiresAt > time.GetUtcNow())
        {
            return request;
        }

        _requests.TryRemove(new KeyValuePair<string, InteractiveAuthRequest>(connection, request));
        return null;
    }
}
