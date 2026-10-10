using System.Net.Http.Headers;
using System.Security.Cryptography;
using Capybara.Gateway.Security;
using Capybara.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Capybara.Gateway.Tests;

/// <summary>Hosts <c>capybarad</c> in memory against the sample config, with runtimes not started.</summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string Login = "owner@example.com";
    public const string Origin = "https://capybara.example.ts.net";

    private readonly TempDirectory _state = new();

    // Generated per run so no token-shaped literal ever lands in the repository.
    public string PhoneToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string DesktopToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Capybara:ConfigRoot", RepositoryPaths.SampleConfig);
        builder.UseSetting("Capybara:StateRoot", _state.Path);
        builder.UseSetting("Capybara:Runtime:AutoStart", "false");
        builder.UseSetting("Capybara:Gateway:AllowedLogin", Login);
        builder.UseSetting("Capybara:Gateway:AllowedOrigins:0", Origin);
        builder.UseSetting("Capybara:Gateway:Devices:0:Name", "phone");
        builder.UseSetting("Capybara:Gateway:Devices:0:Surface", "Phone");
        builder.UseSetting("Capybara:Gateway:Devices:0:Sha256", TailnetAuthenticationMiddleware.HashToken(PhoneToken));
        builder.UseSetting("Capybara:Gateway:Devices:1:Name", "desktop");
        builder.UseSetting("Capybara:Gateway:Devices:1:Surface", "Desktop");
        builder.UseSetting("Capybara:Gateway:Devices:1:Sha256", TailnetAuthenticationMiddleware.HashToken(DesktopToken));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _state.Dispose();
        }
    }
}
