using System.Text.Json.Serialization;
using Fleet.Gateway.Composition;
using Fleet.Gateway.Configuration;
using Fleet.Gateway.Endpoints;
using Fleet.Gateway.Pwa;
using Fleet.Gateway.Realtime;
using Fleet.Gateway.Security;

var builder = WebApplication.CreateBuilder(args);

var gateway = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
builder.WebHost.UseLoopbackOnly(gateway.Port);

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddFleet(builder.Configuration);
builder.Services.AddFleetRealtime();

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
app.MapFleetRealtime();
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Entry point; public for integration tests.</summary>
public partial class Program;
