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

                if (result.ProgressCount > 0)
                {
                    logger.LogInformation(
                        "M6-G quality-gap recovery completed {TerminalCount} terminal gap(s); {DeferredCount} deferred, {VersionBlockedCount} version-blocked, and {CrossWarDeferredCount} cross-war gap(s) remain.",
                        result.TerminalCompleted,
                        result.Deferred,
                        result.VersionBlocked,
                        result.CrossWarDeferred);
                    continue;
                }

                if (result.OutstandingCount > 0)
                {
                    logger.LogDebug(
                        "M6-G quality-gap recovery has no progress; {DeferredCount} deferred, {VersionBlockedCount} version-blocked, and {CrossWarDeferredCount} cross-war gap(s) remain.",
                        result.Deferred,
                        result.VersionBlocked,
                        result.CrossWarDeferred);
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
                    "M6-G local quality-gap recovery iteration failed.");

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
