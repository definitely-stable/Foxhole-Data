using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FoxData.Hosting;

public static class HostingExtensions
{
    public static IHostApplicationBuilder AddFoxDataTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var serviceVersion =
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
            Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        builder
            .AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
                tracing
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter())
            .WithMetrics(metrics =>
                metrics
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddOtlpExporter();
        });

        return builder;
    }
}
