using FoxData.Hosting;
using FoxData.Infrastructure;
using FoxData.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddFoxDataTelemetry(FoxDataServiceNames.Worker);
builder.Services.AddFoxDataInfrastructure(builder.Configuration);

var warApiOptions = WarApiWorkerOptions.FromConfiguration(
    builder.Configuration);

builder.Services.AddSingleton(warApiOptions);
builder.Services.AddSingleton(WarApiCollectionProfile.Bootstrap);
builder.Services.AddSingleton(TimeProvider.System);
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
