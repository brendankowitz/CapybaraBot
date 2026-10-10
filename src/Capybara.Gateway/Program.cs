using System.Text.Json.Serialization;
using Capybara.Contracts.Agents;
using Capybara.Gateway.Composition;
using Capybara.Gateway.Configuration;
using Capybara.Gateway.Endpoints;
using Capybara.Gateway.Pwa;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;
using Capybara.Hosting.Backup;
using Capybara.Hosting.DependencyInjection;

// capybarad backup | capybarad restore <archive> <destination>: run by capybara-backup.service, before any host is built
// so a backup never starts runtimes (FR-HST-05). Settings and BlobRoot are exactly the gateway's.
if (BackupCommand.IsBackupCommand(args))
{
    var configuration = CapybaradConfiguration.Load();
    using var cancel = CapybaradConfiguration.CancelOnCtrlC();
    return await BackupCommand.RunAsync(args, configuration, HostingOptions.FromConfiguration(configuration), Console.Out, Console.Error, cancellationToken: cancel.Token);
}

// capybarad agent <id>: one agent's runtime under its own unit (capybara-agent@.service, Phase 2).
if (args is ["agent", var agentId, .. var rest])
{
    if (!AgentId.TryParse(agentId, out var agent))
    {
        await Console.Error.WriteLineAsync($"capybarad agent: '{agentId}' is not a valid agent id.");
        return 2;
    }

    var agentHost = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = rest, ContentRootPath = AppContext.BaseDirectory });
    agentHost.Services.AddSystemd();
    agentHost.Services.AddCapybaraAgentHost(agentHost.Configuration, agent);
    agentHost.Services.AddCapybaraTelemetry(agentHost.Configuration, instrumentRequests: false);
    await agentHost.Build().RunAsync();
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// Type=notify under systemd (FR-HST-02); a no-op elsewhere.
builder.Services.AddSystemd();

var gateway = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
builder.WebHost.UseLoopbackOnly(gateway.Port);

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddCapybara(builder.Configuration);
builder.Services.AddCapybaraRealtime();
builder.Services.AddCapybaraTelemetry(builder.Configuration, instrumentRequests: true);

var app = builder.Build();

app.UseExceptionHandler();
app.UseWebSockets();
app.UseMiddleware<TailnetAuthenticationMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet(TailnetAuthenticationMiddleware.HealthPath, () => TypedResults.Text("ok"));
app.MapPwaShellFallback();
app.MapAgentEndpoints();
app.MapSafetyEndpoints();
app.MapObservabilityEndpoints();
app.MapCapybaraRealtime();
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

/// <summary>Entry point; public for integration tests.</summary>
public partial class Program;
