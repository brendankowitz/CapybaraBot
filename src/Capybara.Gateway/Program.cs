using System.Text.Json.Serialization;
using Capybara.Gateway.Composition;
using Capybara.Gateway.Configuration;
using Capybara.Gateway.Endpoints;
using Capybara.Gateway.Pwa;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;

var builder = WebApplication.CreateBuilder(args);

var gateway = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
builder.WebHost.UseLoopbackOnly(gateway.Port);

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddCapybara(builder.Configuration);
builder.Services.AddCapybaraRealtime();

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

/// <summary>Entry point; public for integration tests.</summary>
public partial class Program;
