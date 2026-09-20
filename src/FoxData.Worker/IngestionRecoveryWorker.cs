using FoxData.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class IngestionRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    ILogger<IngestionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var recovery = scope.ServiceProvider.GetRequiredService<IngestionRecovery>();
                var result = await recovery.RecoverExpiredAsync(stoppingToken);

                if (result.Count > 0)
                {
                    logger.LogInformation(
                        "Recovered {RecoveryCount} expired ingestion item(s).",
                        result.Count);
                    continue;
                }

                await Task.Delay(options.RecoveryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Ingestion recovery iteration failed.");

                await DelayAfterFailureAsync(stoppingToken);
            }
        }
    }

    private async Task DelayAfterFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.RecoveryInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
