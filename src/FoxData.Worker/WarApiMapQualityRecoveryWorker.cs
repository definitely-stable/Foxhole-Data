using FoxData.Application.Canonical;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

// M6-E crash repair only: snapshots are already normalized from durable raw
// evidence. M6-G will generalize earlier parse/normalization and 304 recovery.
public sealed class WarApiMapQualityRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    ILogger<WarApiMapQualityRecoveryWorker> logger)
    : BackgroundService
{
    private const int BatchSize = 64;
    private static readonly TimeSpan RecoveryInterval =
        TimeSpan.FromSeconds(15);

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
                var (completed, deferred) =
                    await RunOnceAsync(stoppingToken);
                if (completed != 0 || deferred != 0)
                {
                    logger.LogDebug(
                        "M6-E quality replay: {Completed} terminal, {Deferred} deferred.",
                        completed,
                        deferred);
                }
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
                    "M6-E local quality recovery pass failed.");
            }

            try
            {
                await Task.Delay(
                    RecoveryInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<(int Completed, int Deferred)> RunOnceAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var pending = scope.ServiceProvider
            .GetRequiredService<IMapQualityPendingReader>();
        var snapshots = scope.ServiceProvider
            .GetRequiredService<MapSnapshotKernel>();
        var coordinator = scope.ServiceProvider
            .GetRequiredService<WarApiMapQualityCoordinator>();

        DateTimeOffset? afterAt = null;
        FoxData.Core.Evidence.FetchId? afterFetchId = null;
        var completed = 0;
        var deferred = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await pending.GetPendingAsync(
                WarApiCatalog.SourceKey,
                WarApiVersions.MapTaxonomy,
                WarApiVersions.MapQualityPolicy,
                afterAt,
                afterFetchId,
                BatchSize,
                cancellationToken);

            foreach (var candidate in batch)
            {
                var normalized =
                    await snapshots.GetByNormalizationRunAsync(
                        candidate.NormalizationRunId,
                        cancellationToken)
                    ?? throw new CanonicalStateIntegrityException(
                        "Pending map quality candidate lost its durable normalized snapshot.");

                var result = await coordinator.EvaluateAsync(
                    normalized,
                    candidate.ShardId,
                    candidate.RepresentationFetchId,
                    candidate.RetrievedAt,
                    cancellationToken);

                if (result.Status == WarApiMapQualityStatus.Deferred)
                {
                    deferred++;
                }
                else
                {
                    completed++;
                }

                afterAt = candidate.RetrievedAt;
                afterFetchId = candidate.RepresentationFetchId;
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return (completed, deferred);
    }
}
