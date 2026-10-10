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

const int ExitUsage = 2;
const int ExitInterrupted = 130;
const string Usage = "Usage: capybarad [--key=value ...] | capybarad backup | capybarad restore <archive> <destination> | capybarad agent <id>";

// capybarad backup | capybarad restore <archive> <destination>: run by capybara-backup.service, before any host is built
// so a backup never starts runtimes (FR-HST-05). Settings and BlobRoot are exactly the gateway's.
if (BackupCommand.IsBackupCommand(args))
{
    var configuration = CapybaradConfiguration.Load();
    using var cancel = CapybaradConfiguration.CancelOnCtrlC();
    try
    {
        return await BackupCommand.RunAsync(args, configuration, HostingOptions.FromConfiguration(configuration), Console.Out, Console.Error, cancellationToken: cancel.Token);
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
        await Console.Error.WriteLineAsync($"capybarad {args[0]} interrupted; nothing was completed.");
        return ExitInterrupted;
    }
}

// capybarad agent <id>: one agent's runtime under its own unit (capybara-agent@.service, Phase 2).
if (args is ["agent", ..])
{
    if (args is not ["agent", var agentId, .. var rest] || !AgentId.TryParse(agentId, out var agent))
    {
        await Console.Error.WriteLineAsync($"capybarad agent: give a valid agent id.{Environment.NewLine}{Usage}");
        return ExitUsage;
    }

    var agentHost = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = rest, ContentRootPath = AppContext.BaseDirectory });
    agentHost.Services.AddSystemd();
    agentHost.Services.AddCapybaraAgentHost(agentHost.Configuration, agent);
    agentHost.Services.AddCapybaraTelemetry(agentHost.Configuration, instrumentRequests: false);
    using var host = agentHost.Build();
    LogCompositionWarnings(host.Services);
    await host.RunAsync();
    return 0;
}

// Anything else that is not a configuration override (--key=value, key=value) is a mistyped verb: never start the
// gateway because of a typo in a unit file.
if (args.Length > 0 && !args[0].StartsWith('-') && !args[0].Contains('=', StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync($"capybarad: unknown command '{args[0]}'.{Environment.NewLine}{Usage}");
    return ExitUsage;
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
LogCompositionWarnings(app.Services);

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseWebSockets();
app.UseMiddleware<TailnetAuthenticationMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet(TailnetAuthenticationMiddleware.HealthPath, () => TypedResults.Text("ok"));
app.MapCapybaraApi();
app.MapCapybaraRealtime();
app.MapPwaShell();

await app.RunAsync();
return 0;

static void LogCompositionWarnings(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Capybara.Composition");
    foreach (var warning in services.GetRequiredService<CompositionWarnings>().Messages)
    {
#pragma warning disable CA1848, CA2254 // Once per start; the messages are composed from configuration keys, not user data.
        logger.LogWarning(warning);
#pragma warning restore CA1848, CA2254
    }
}

/// <summary>Entry point; public for integration tests.</summary>
public partial class Program;
