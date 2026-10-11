using Capybara.Contracts.Diagnostics;
using Capybara.Gateway.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Capybara.Gateway.Composition;

/// <summary>
/// OpenTelemetry for capybarad (FR-OBS-01, FR-OBS-04): traces from every <c>Capybara.*</c> source (gate decisions,
/// approvals, A2A hops, routines, connector calls) plus incoming requests, and the <c>Capybara</c> meters. Export goes
/// to a local OTLP collector only when <c>Capybara:Telemetry:OtlpEndpoint</c> is set; nothing leaves the box otherwise.
/// </summary>
/// <remarks>
/// Content capture is off: no source puts prompts, tool arguments or results on spans, and outgoing HTTP is deliberately
/// not instrumented, because client spans record full URLs and Telegram puts its bot token in the URL path. Incoming
/// request query strings (approval nonces, SignalR access tokens) are redacted by the ASP.NET Core instrumentation.
/// </remarks>
public static class TelemetrySetup
{
    public const string ServiceName = "capybarad";

    public static IServiceCollection AddCapybaraTelemetry(this IServiceCollection services, IConfiguration configuration, bool instrumentRequests)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(CapybaraTelemetry.Prefix + ".*");
                if (instrumentRequests)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(CapybaraTelemetry.Prefix, CapybaraTelemetry.Prefix + ".*");
                if (instrumentRequests)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }
            });

        if (options.OtlpEndpoint is { } endpoint)
        {
            builder.UseOtlpExporter(Protocol(options.OtlpProtocol), endpoint);
        }

        return services;
    }

    private static OtlpExportProtocol Protocol(string value) => value.ToUpperInvariant() switch
    {
        "GRPC" => OtlpExportProtocol.Grpc,
        "HTTP/PROTOBUF" => OtlpExportProtocol.HttpProtobuf,
        _ => throw new InvalidOperationException($"{TelemetryOptions.SectionName}:OtlpProtocol '{value}' must be http/protobuf or grpc."),
    };
}
