using FoxData.Application.Sources;
using FoxData.Core.Sources;

namespace FoxData.UnitTests;

public sealed class SourceRegistryValidationTests
{
    private readonly SourceRegistry _registry = new(new ThrowingStore());

    [Fact]
    public async Task RegistrationRejectsWhitespacePaddedIdentity()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _registry.RegisterSourceAsync(" source ", "Fixture", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ShardRegistrationRejectsEmptySourceId()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _registry.RegisterShardAsync(
                new SourceId(Guid.Empty),
                "live-1",
                "Live 1",
                "live",
                TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingStore : ISourceRegistryStore
    {
        public Task<RegistryRegistrationResult<SourceDescriptor>> RegisterSourceAsync(
            SourceId proposedId,
            string key,
            string displayName,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<RegistryRegistrationResult<ShardDescriptor>> RegisterShardAsync(
            ShardId proposedId,
            SourceId sourceId,
            string key,
            string displayName,
            string environment,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<RegistryRegistrationResult<EndpointDescriptor>> RegisterEndpointAsync(
            EndpointId proposedId,
            ShardId shardId,
            string capabilityKey,
            string semanticKey,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<SourceDescriptor?> GetSourceByKeyAsync(
            string key,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ShardDescriptor?> GetShardByKeyAsync(
            SourceId sourceId,
            string key,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<EndpointDescriptor?> GetEndpointBySemanticKeyAsync(
            ShardId shardId,
            string semanticKey,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
