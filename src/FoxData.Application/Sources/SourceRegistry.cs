using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public sealed class SourceRegistry(ISourceRegistryStore store)
{
    public Task<RegistryRegistrationResult<SourceDescriptor>> RegisterSourceAsync(
        string key,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        return store.RegisterSourceAsync(
            SourceId.New(),
            ValidateIdentityToken(key, nameof(key), 128),
            ValidateDisplayName(displayName, nameof(displayName)),
            cancellationToken);
    }

    public Task<RegistryRegistrationResult<ShardDescriptor>> RegisterShardAsync(
        SourceId sourceId,
        string key,
        string displayName,
        string environment,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(sourceId.Value, nameof(sourceId));

        return store.RegisterShardAsync(
            ShardId.New(),
            sourceId,
            ValidateIdentityToken(key, nameof(key), 128),
            ValidateDisplayName(displayName, nameof(displayName)),
            ValidateIdentityToken(environment, nameof(environment), 64),
            cancellationToken);
    }

    public Task<RegistryRegistrationResult<EndpointDescriptor>> RegisterEndpointAsync(
        ShardId shardId,
        string capabilityKey,
        string semanticKey,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(shardId.Value, nameof(shardId));

        return store.RegisterEndpointAsync(
            EndpointId.New(),
            shardId,
            ValidateIdentityToken(capabilityKey, nameof(capabilityKey), 128),
            ValidateIdentityToken(semanticKey, nameof(semanticKey), 256),
            cancellationToken);
    }

    public Task<SourceDescriptor?> GetSourceByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        return store.GetSourceByKeyAsync(
            ValidateIdentityToken(key, nameof(key), 128),
            cancellationToken);
    }

    public Task<ShardDescriptor?> GetShardByKeyAsync(
        SourceId sourceId,
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(sourceId.Value, nameof(sourceId));

        return store.GetShardByKeyAsync(
            sourceId,
            ValidateIdentityToken(key, nameof(key), 128),
            cancellationToken);
    }

    public Task<EndpointDescriptor?> GetEndpointBySemanticKeyAsync(
        ShardId shardId,
        string semanticKey,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(shardId.Value, nameof(shardId));

        return store.GetEndpointBySemanticKeyAsync(
            shardId,
            ValidateIdentityToken(semanticKey, nameof(semanticKey), 256),
            cancellationToken);
    }

    private static string ValidateIdentityToken(string value, string parameterName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maxLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Value must be non-empty, already trimmed, and at most {maxLength} characters.",
                parameterName);
        }

        return value;
    }

    private static string ValidateDisplayName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Display name must be non-empty, already trimmed, and at most 256 characters.",
                parameterName);
        }

        return value;
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }
}
