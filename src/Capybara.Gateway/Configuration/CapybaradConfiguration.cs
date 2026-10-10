namespace Capybara.Gateway.Configuration;

/// <summary>
/// Configuration for the command-line modes that run without a host (<c>capybarad backup</c>/<c>restore</c>): the same
/// sources the gateway reads, minus the command line (whose positional arguments are the command's).
/// </summary>
internal static class CapybaradConfiguration
{
    public static IConfiguration Load()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>Cancelled on Ctrl+C, so an interactive backup stops cleanly instead of being killed mid-archive.</summary>
    public static CancellationTokenSource CancelOnCtrlC()
    {
        var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        return cancel;
    }
}
