using FoxData.Application.Ingestion;
using FoxData.Core.Ingestion;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiExecutorWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    WarApiCollectionProfile collectionProfile,
    ILogger<WarApiExecutorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var loops = Enumerable
            .Range(0, collectionProfile.ExecutorConcurrency)
            .Select(
                _ => RunExecutorLoopAsync(
                    WorkerInstanceId.New(),
                    stoppingToken))
            .ToArray();

        await Task.WhenAll(loops);
    }

    private async Task RunExecutorLoopAsync(
        WorkerInstanceId workerId,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope =
                    scopeFactory.CreateAsyncScope();
                var ingestion =
                    scope.ServiceProvider
                        .GetRequiredService<IngestionKernel>();

                var claim =
                    await ingestion.ClaimNextForSourceAsync(
                        workerId,
                        WarApiCatalog.SourceKey,
                        options.LeaseDuration,
                        stoppingToken);

                if (!claim.Claimed)
                {
                    await Task.Delay(
                        options.IdleDelay,
                        stoppingToken);
                    continue;
                }

                var executor =
                    scope.ServiceProvider
                        .GetRequiredService<
                            WarApiAttemptExecutor>();

                await executor.ExecuteAsync(
                    claim.Job!,
                    workerId,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "War API executor iteration failed for worker {WorkerId}.",
                    workerId);
                await DelayAfterFailureAsync(
                    stoppingToken);
            }
        }
    }

    private async Task DelayAfterFailureAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(
                options.IdleDelay,
                stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
