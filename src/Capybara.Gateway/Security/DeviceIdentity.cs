using System.Diagnostics.CodeAnalysis;
using Capybara.Policy.Safety;

namespace Capybara.Gateway.Security;

/// <summary>The authenticated device behind a request.</summary>
/// <remarks>
/// <see cref="Login"/> is the Tailscale identity and <see cref="Device"/> the configured device name; both come from the
/// authentication middleware, never from a request body. Handlers name human principals from them.
/// </remarks>
public sealed record DeviceIdentity(string Login, string Device, ControlSurface Surface)
{
    /// <summary>How human actions taken on this device are attributed (audit, config commits, routine changes).</summary>
    public string PrincipalName => $"{Login} ({Device})";
}

public static class DeviceIdentityHttpContextExtensions
{
    private const string Key = "capybara.device";
    private const string BrokerKey = "capybara.broker";

    public static void SetDevice(this HttpContext context, DeviceIdentity device) => context.Items[Key] = device;

    public static bool TryGetDevice(this HttpContext context, [NotNullWhen(true)] out DeviceIdentity? device)
    {
        device = context.Items.TryGetValue(Key, out var value) ? value as DeviceIdentity : null;
        return device is not null;
    }

    /// <exception cref="InvalidOperationException">No device authenticated this request (fails closed).</exception>
    public static DeviceIdentity GetDevice(this HttpContext context) =>
        context.TryGetDevice(out var device) ? device : throw new InvalidOperationException("Request was not authenticated.");

    /// <summary>Marks a request authenticated with the broker's key (only <c>POST /api/connections/auth-requests</c>).</summary>
    public static void SetBrokerCaller(this HttpContext context) => context.Items[BrokerKey] = true;

    public static bool IsBrokerCaller(this HttpContext context) => context.Items.TryGetValue(BrokerKey, out var value) && value is true;
}
