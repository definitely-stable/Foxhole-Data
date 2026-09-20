using FoxData.Application.Sources;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiPlannerWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    ILogger<WarApiPlannerWorker> logger) : BackgroundService
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
                var planningReader =
                    scope.ServiceProvider.GetRequiredService<ISourcePlanningReader>();

                var endpointIds =
                    await planningReader.ListPendingCurrentEndpointsAsync(
                        WarApiCatalog.SourceKey,
                        options.PlannerBatchSize,
                        stoppingToken);

                if (endpointIds.Count == 0)
                {
                    await Task.Delay(options.PlannerInterval, stoppingToken);
                    continue;
                }

                var reconciler =
                    scope.ServiceProvider.GetRequiredService<WarApiReconciler>();

                foreach (var endpointId in endpointIds)
                {
                    try
                    {
                        await reconciler.ReconcileAsync(
                            endpointId,
                            stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "War API reconciliation failed for endpoint {EndpointId}.",
                            endpointId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "War API planner iteration failed.");
                await DelayAfterFailureAsync(stoppingToken);
            }
        }
    }

    private async Task DelayAfterFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.PlannerInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
