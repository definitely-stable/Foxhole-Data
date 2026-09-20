using FoxData.Application.Sources;
using FoxData.Core.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed record WarApiRegistryContext(
    SourceDescriptor Source,
    ShardDescriptor Shard,
    EndpointDescriptor Endpoint,
    WarApiShard WarApiShard,
    SourceEndpoint SourceEndpoint);

public sealed class WarApiRegistryResolver(SourceRegistry registry)
{
    public async Task<WarApiRegistryContext> ResolveAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        var endpoint = await registry.GetEndpointAsync(endpointId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Collection endpoint {endpointId} does not exist.");

        var shard = await registry.GetShardAsync(endpoint.ShardId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Shard {endpoint.ShardId} for endpoint {endpointId} does not exist.");

        var source = await registry.GetSourceAsync(shard.SourceId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Source {shard.SourceId} for shard {shard.Id} does not exist.");

        if (!string.Equals(
                source.Key,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint {endpointId} belongs to unsupported source '{source.Key}'.");
        }

        var warApiShard = WarApiCatalog.ParseShardKey(shard.Key);
        var expectedEnvironment = WarApiCatalog.GetEnvironment(warApiShard);

        if (!string.Equals(
                shard.Environment,
                expectedEnvironment,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Shard '{shard.Key}' environment '{shard.Environment}' does not match '{expectedEnvironment}'.");
        }

        var sourceEndpoint = WarApiCatalog.FromRegistry(
            endpoint.CapabilityKey,
            endpoint.SemanticKey);

        return new WarApiRegistryContext(
            source,
            shard,
            endpoint,
            warApiShard,
            sourceEndpoint);
    }
}
