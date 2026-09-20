using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class BootstrapWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<BootstrapWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "Official War API ingestion is disabled by configuration.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IngestionKernel>();

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            stoppingToken);

        EnsureCompatible(source.Status, "source", WarApiCatalog.SourceKey);

        foreach (var shard in options.Shards)
        {
            await BootstrapShardAsync(
                registry,
                ingestion,
                source.Resource,
                shard,
                stoppingToken);
        }

        logger.LogInformation(
            "Official War API registry bootstrap completed for {ShardCount} shard(s).",
            options.Shards.Count);
    }

    private async Task BootstrapShardAsync(
        SourceRegistry registry,
        IngestionKernel ingestion,
        SourceDescriptor source,
        WarApiShard shard,
        CancellationToken cancellationToken)
    {
        var shardKey = WarApiCatalog.GetShardKey(shard);
        var shardRegistration = await registry.RegisterShardAsync(
            source.Id,
            shardKey,
            DisplayName(shard),
            WarApiCatalog.GetEnvironment(shard),
            cancellationToken);

        EnsureCompatible(shardRegistration.Status, "shard", shardKey);

        foreach (var endpoint in new[] { WarApiCatalog.War(), WarApiCatalog.Maps() })
        {
            var endpointRegistration = await registry.RegisterEndpointAsync(
                shardRegistration.Resource.Id,
                endpoint.Capability.Key,
                endpoint.SemanticKey,
                cancellationToken);

            EnsureCompatible(
                endpointRegistration.Status,
                "endpoint",
                $"{shardKey}/{endpoint.SemanticKey}");

            var now = timeProvider.GetUtcNow();
            var spreadWindow = TimeSpan.FromSeconds(15);
            var spread = WarApiResponsePolicy.Spread(
                shardKey,
                endpoint.SemanticKey,
                spreadWindow);
            var target = now + spread;

            var job = await ingestion.EnqueueAsync(
                endpointRegistration.Resource.Id,
                $"bootstrap@1:{shardKey}:{endpoint.SemanticKey}",
                target,
                target,
                cancellationToken: cancellationToken);

            if (job.Status is JobEnqueueStatus.Conflict)
            {
                logger.LogDebug(
                    "Bootstrap job already exists with an earlier schedule for {Shard}/{Endpoint}.",
                    shardKey,
                    endpoint.SemanticKey);
            }
        }
    }

    private static void EnsureCompatible(
        RegistryRegistrationStatus status,
        string kind,
        string key)
    {
        if (status is RegistryRegistrationStatus.Conflict)
        {
            throw new InvalidOperationException(
                $"War API {kind} registry conflict for '{key}'.");
        }
    }

    private static string DisplayName(WarApiShard shard) =>
        shard switch
        {
            WarApiShard.Live1 => "Live 1",
            WarApiShard.Live2 => "Live 2",
            WarApiShard.Live3 => "Live 3",
            WarApiShard.Dev => "Dev",
            _ => throw new ArgumentOutOfRangeException(nameof(shard), shard, null),
        };
}
