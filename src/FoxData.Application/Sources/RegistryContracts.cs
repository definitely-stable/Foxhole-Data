using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public enum RegistryRegistrationStatus
{
    Created,
    Existing,
    Conflict,
}

public sealed record SourceDescriptor(
    SourceId Id,
    string Key,
    string DisplayName,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ShardDescriptor(
    ShardId Id,
    SourceId SourceId,
    string Key,
    string DisplayName,
    string Environment,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EndpointDescriptor(
    EndpointId Id,
    ShardId ShardId,
    string CapabilityKey,
    string SemanticKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RegistryRegistrationResult<T>(
    RegistryRegistrationStatus Status,
    T Resource)
    where T : notnull;
