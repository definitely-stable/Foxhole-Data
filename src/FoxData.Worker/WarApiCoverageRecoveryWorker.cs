using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiCoverageRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    ILogger<WarApiCoverageRecoveryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope =
                    scopeFactory.CreateAsyncScope();
                var recovery =
                    scope.ServiceProvider.GetRequiredService<
                        WarApiCoverageRecoveryCoordinator>();

                var result = await recovery.RunOnceAsync(
                    stoppingToken);

                if (result.ProgressCount > 0)
                {
                    logger.LogInformation(
                        "M5 coverage recovery recorded {CoverageCount} coverage rows, repaired {ParseCount} parse runs, applied {ContinuityApplied} continuity rows, rejected {ContinuityRejected} continuity rows, completed {CanonicalCount} canonical gaps; {DeferredCount} item(s) remain deferred.",
                        result.CoverageRecorded,
                        result.ParseRunsRepaired,
                        result.ContinuityApplied,
                        result.ContinuityRejected,
                        result.CanonicalCompleted,
                        result.CanonicalDeferred);
                    continue;
                }

                await Task.Delay(
                    options.RecoveryInterval,
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
                    "M5 coverage/reprocessing recovery iteration failed.");

                try
                {
                    await Task.Delay(
                        options.RecoveryInterval,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
