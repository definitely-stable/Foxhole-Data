using FoxData.Hosting;
using FoxData.Infrastructure;
using FoxData.Sources.WarApi;
using FoxData.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddFoxDataTelemetry(FoxDataServiceNames.Worker);
builder.Services.AddFoxDataInfrastructure(builder.Configuration);

var warApiOptions = WarApiWorkerOptions.FromConfiguration(
    builder.Configuration);

builder.Services.AddSingleton(warApiOptions);
builder.Services.AddSingleton(
    WarApiMeasurementProbeConfiguration.FromConfiguration(
        builder.Configuration,
        warApiOptions));
builder.Services.AddSingleton(
    WarApiCollectionPolicyConfiguration.FromConfiguration(
        builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(
    serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<WarApiWorkerOptions>();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        return new WarApiOutboundRateGovernor(
            timeProvider,
            options.OutboundGlobalMinimumInterval,
            options.OutboundPerHostMinimumInterval);
    });
builder.Services.AddSingleton<WarApiTransport>();
builder.Services.AddSingleton<IWarApiTransport>(
    serviceProvider => serviceProvider.GetRequiredService<WarApiTransport>());
builder.Services.AddScoped<WarApiRegistryResolver>();
builder.Services.AddScoped<WarApiAttemptExecutor>();
builder.Services.AddScoped<WarApiReconciler>();

builder.Services.AddHostedService<BootstrapWorker>();
builder.Services.AddHostedService<IngestionRecoveryWorker>();
builder.Services.AddHostedService<WarApiPlannerWorker>();
builder.Services.AddHostedService<WarApiExecutorWorker>();

await builder.Build().RunAsync();
