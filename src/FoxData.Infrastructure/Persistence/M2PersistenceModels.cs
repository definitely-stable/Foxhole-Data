namespace FoxData.Infrastructure.Persistence;

internal sealed class SourceRow
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ShardRow
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public required string Environment { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class EndpointRow
{
    public Guid Id { get; set; }
    public Guid ShardId { get; set; }
    public required string CapabilityKey { get; set; }
    public required string SemanticKey { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class CollectionJobRow
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public short Priority { get; set; }
    public required string State { get; set; }
    public Guid? LeaseOwnerId { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class IngestionAttemptRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNumber { get; set; }
    public long LeaseGeneration { get; set; }
    public long? FenceToken { get; set; }
    public required string State { get; set; }
    public string? OutcomeCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ExchangeAuthorizedAt { get; set; }
    public DateTimeOffset? RawDurableAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public string? ErrorClass { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class EndpointStateRow
{
    public Guid EndpointId { get; set; }
    public long FenceToken { get; set; }
    public Guid? ActiveAttemptId { get; set; }
    public Guid? LastAuthoritativeAttemptId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class PayloadRow
{
    public Guid Id { get; set; }
    public required byte[] Sha256 { get; set; }
    public long ByteLength { get; set; }
    public required byte[] Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class FetchRow
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid EndpointId { get; set; }
    public DateTimeOffset RequestStartedAt { get; set; }
    public DateTimeOffset? ResponseStartedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public required string TransportKind { get; set; }
    public int? StatusCode { get; set; }
    public string? MediaType { get; set; }
    public string? ContentEncoding { get; set; }
    public long? DeclaredLength { get; set; }
    public string? SourceEtag { get; set; }
    public string? CacheControl { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? PayloadId { get; set; }
    public Guid? PriorFetchId { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
