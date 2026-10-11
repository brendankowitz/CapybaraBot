using System.Net.Http.Headers;
using System.Security.Cryptography;
using Capybara.Channels;
using Capybara.Connections.DependencyInjection;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;
using Capybara.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Capybara.Gateway.Tests;

/// <summary>
/// Hosts <c>capybarad</c> in memory with runtimes not started, against a private copy of the sample config (lifecycle
/// and proposal tests write and git-commit it). The broker, SignalR and push channels are fakes that record what they
/// receive; time is a <see cref="FakeTimeProvider"/>.
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    public const string Login = "owner@example.com";
    public const string Origin = "https://capybara.example.ts.net";

    private readonly TempDirectory _root = new();

    public GatewayFactory()
    {
        CopyDirectory(RepositoryPaths.SampleConfig, ConfigRoot);
        Directory.CreateDirectory(StateRoot);
    }

    // Generated per run so no token-shaped literal ever lands in the repository.
    public string PhoneToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string DesktopToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The public webhook relay's token: it may only post events.</summary>
    public string RelayToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string ConfigRoot => Path.Combine(_root.Path, "config");

    public string StateRoot => Path.Combine(_root.Path, "state");

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    public FakeBrokerHandler Broker { get; } = new();

    public RecordingEvents Events { get; } = new();

    public RecordingChannel Channel { get; } = new();

    /// <summary>The service registrations as composed, for checking that nothing is registered twice.</summary>
    public IReadOnlyList<ServiceDescriptor> Registrations { get; private set; } = [];

    public HttpClient CreateDeviceClient(string? token, string? login = Login, string? origin = null)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (login is not null)
        {
            client.DefaultRequestHeaders.Add(TailnetAuthenticationMiddleware.TailscaleLoginHeader, login);
        }

        if (origin is not null)
        {
            client.DefaultRequestHeaders.Add("Origin", origin);
        }

        return client;
    }

    /// <summary>Extra settings for a test-specific host; applied after the defaults.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings() => [];

    /// <summary>Extra service replacements for a test-specific host; applied after the standard fakes.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Capybara:ConfigRoot", ConfigRoot);
        builder.UseSetting("Capybara:StateRoot", StateRoot);
        builder.UseSetting("Capybara:Runtime:AutoStart", "false");
        builder.UseSetting("Capybara:Gateway:AllowedLogin", Login);
        builder.UseSetting("Capybara:Gateway:AllowedOrigins:0", Origin);
        builder.UseSetting("Capybara:Gateway:Devices:0:Name", "phone");
        builder.UseSetting("Capybara:Gateway:Devices:0:Surface", "Phone");
        builder.UseSetting("Capybara:Gateway:Devices:0:Sha256", TailnetAuthenticationMiddleware.HashToken(PhoneToken));
        builder.UseSetting("Capybara:Gateway:Devices:1:Name", "desktop");
        builder.UseSetting("Capybara:Gateway:Devices:1:Surface", "Desktop");
        builder.UseSetting("Capybara:Gateway:Devices:1:Sha256", TailnetAuthenticationMiddleware.HashToken(DesktopToken));
        builder.UseSetting("Capybara:Gateway:Devices:2:Name", "webhook-relay");
        builder.UseSetting("Capybara:Gateway:Devices:2:Surface", "EventRelay");
        builder.UseSetting("Capybara:Gateway:Devices:2:Sha256", TailnetAuthenticationMiddleware.HashToken(RelayToken));
        foreach (var (key, value) in Settings())
        {
            builder.UseSetting(key, value);
        }

        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.ConfigureTestServices(services =>
        {
            Registrations = [.. services];
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<ICapybaraEvents>();
            services.AddSingleton<ICapybaraEvents>(Events);
            services.AddSingleton<INotificationChannel>(Channel);
            services.AddHttpClient(BrokerClientServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Broker);
            ConfigureServices(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root.Path))
        {
            // git marks its object files read-only, which Windows refuses to delete.
            foreach (var file in Directory.EnumerateFiles(_root.Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            _root.Dispose();
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
