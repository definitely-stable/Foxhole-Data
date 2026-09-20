using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public interface ISourceRegistryStore
{
    Task<RegistryRegistrationResult<SourceDescriptor>> RegisterSourceAsync(
        SourceId proposedId,
        string key,
        string displayName,
        CancellationToken cancellationToken);

    Task<RegistryRegistrationResult<ShardDescriptor>> RegisterShardAsync(
        ShardId proposedId,
        SourceId sourceId,
        string key,
        string displayName,
        string environment,
        CancellationToken cancellationToken);

    Task<RegistryRegistrationResult<EndpointDescriptor>> RegisterEndpointAsync(
        EndpointId proposedId,
        ShardId shardId,
        string capabilityKey,
        string semanticKey,
        CancellationToken cancellationToken);

    Task<SourceDescriptor?> GetSourceByKeyAsync(
        string key,
        CancellationToken cancellationToken);

    Task<SourceDescriptor?> GetSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken);

    Task<ShardDescriptor?> GetShardByKeyAsync(
        SourceId sourceId,
        string key,
        CancellationToken cancellationToken);

    Task<ShardDescriptor?> GetShardAsync(
        ShardId shardId,
        CancellationToken cancellationToken);

    Task<EndpointDescriptor?> GetEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken);

    Task<EndpointDescriptor?> GetEndpointBySemanticKeyAsync(
        ShardId shardId,
        string semanticKey,
        CancellationToken cancellationToken);
}
