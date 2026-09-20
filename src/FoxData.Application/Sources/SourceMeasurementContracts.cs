using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public sealed record SourceMeasurementFetch(
    FetchId FetchId,
    EndpointId EndpointId,
    string SourceKey,
    string ShardKey,
    string Environment,
    string CapabilityKey,
    string SemanticKey,
    DateTimeOffset RequestStartedAt,
    DateTimeOffset RetrievedAt,
    int? StatusCode,
    long DurationMs,
    string? PayloadSha256Hex,
    long? PayloadBytes,
    string? SourceEtag,
    string? CacheControl,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? SourceDate,
    long? SourceAgeSeconds,
    string? RetryAfter,
    string? ContentEncoding,
    long? DeclaredLength,
    string? BodyErrorCode);

public sealed record SourceMeasurementAttempt(
    IngestionAttemptId AttemptId,
    CollectionJobId JobId,
    EndpointId EndpointId,
    string SourceKey,
    string ShardKey,
    string Environment,
    string CapabilityKey,
    string SemanticKey,
    DateTimeOffset ScheduledFor,
    DateTimeOffset AvailableAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExchangeAuthorizedAt,
    DateTimeOffset? RawDurableAt,
    DateTimeOffset? CompletedAt,
    string State,
    string? OutcomeCode,
    string? ErrorClass,
    string? ErrorCode);

public interface ISourceMeasurementReader
{
    IAsyncEnumerable<SourceMeasurementFetch> ReadFetchesAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SourceMeasurementAttempt> ReadAttemptsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        CancellationToken cancellationToken);
}
