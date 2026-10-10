using Fleet.Policy.Safety;

namespace Fleet.Gateway.Security;

/// <summary>The authenticated device behind a request.</summary>
public sealed record DeviceIdentity(string Login, string Device, ControlSurface Surface);

public static class DeviceIdentityHttpContextExtensions
{
    private const string Key = "fleet.device";

    public static void SetDevice(this HttpContext context, DeviceIdentity device) => context.Items[Key] = device;

    public static DeviceIdentity GetDevice(this HttpContext context) =>
        context.Items.TryGetValue(Key, out var value) && value is DeviceIdentity device
            ? device
            : throw new InvalidOperationException("Request was not authenticated.");
}
