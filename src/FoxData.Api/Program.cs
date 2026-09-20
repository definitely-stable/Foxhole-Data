using FoxData.Hosting;
using FoxData.Infrastructure;
using FoxData.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddFoxDataTelemetry(FoxDataServiceNames.Api);

builder.Services.ConfigureOpenTelemetryTracerProvider(
    static tracing => tracing.AddAspNetCoreInstrumentation());
builder.Services.ConfigureOpenTelemetryMeterProvider(
    static metrics => metrics.AddAspNetCoreInstrumentation());

builder.Services.AddFoxDataInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        static () => HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddCheck<PostgresHealthCheck>(
        "postgres",
        tags: ["ready"]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live", StringComparer.Ordinal),
    });

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready", StringComparer.Ordinal),
    });

app.Run();

public partial class Program;
