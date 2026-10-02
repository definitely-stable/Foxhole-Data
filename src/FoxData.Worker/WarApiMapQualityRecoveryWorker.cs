using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiMapQualityRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    ILogger<WarApiMapQualityRecoveryWorker> logger)
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
                        WarApiMapQualityRecoveryCoordinator>();

                var result = await recovery.RunOnceAsync(
                    stoppingToken);

                if (result.ProgressCount > 0 ||
                    result.OutstandingCount > 0)
                {
                    logger.LogInformation(
                        "M6-G5 quality-gap recovery completed {TerminalCount} terminal gap(s); {DeferredCount} deferred and {VersionBlockedCount} version-blocked gap(s) remain.",
                        result.TerminalCompleted,
                        result.Deferred,
                        result.VersionBlocked);
                }

                if (result.ProgressCount > 0)
                {
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
                    "M6-G5 local quality-gap recovery iteration failed.");

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
