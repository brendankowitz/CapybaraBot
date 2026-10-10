using System.Net;

namespace Capybara.Gateway.Security;

/// <summary>The gateway binds to loopback only; Tailscale Serve is the sole way in (FR-CLI-01).</summary>
public static class LoopbackGuard
{
    public static IWebHostBuilder UseLoopbackOnly(this IWebHostBuilder builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        return builder.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
        });
    }
}
