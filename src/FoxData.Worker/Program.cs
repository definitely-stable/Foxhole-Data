using FoxData.Hosting;
using FoxData.Infrastructure;
using FoxData.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddFoxDataTelemetry(FoxDataServiceNames.Worker);
builder.Services.AddFoxDataInfrastructure(builder.Configuration);
builder.Services.AddHostedService<BootstrapWorker>();

await builder.Build().RunAsync();
