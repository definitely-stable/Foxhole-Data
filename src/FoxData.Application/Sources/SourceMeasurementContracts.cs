using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public sealed record SourceMeasurementFetch(
    FetchId FetchId,
    IngestionAttemptId AttemptId,
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
    int AttemptNumber,
    string JobIdempotencyKey,
    string SourceKey,
    string ShardKey,
    string Environment,
    string CapabilityKey,
    string SemanticKey,
    DateTimeOffset ScheduledFor,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExchangeAuthorizedAt,
    DateTimeOffset? RawDurableAt,
    DateTimeOffset? CompletedAt,
    string State,
    string? OutcomeCode,
    string? ErrorClass,
    string? ErrorCode);

public sealed record SourceMeasurementParseRun(
    SourceParseRunId ParseRunId,
    FetchId RepresentationFetchId,
    EndpointId EndpointId,
    string SourceKey,
    string ShardKey,
    string Environment,
    string CapabilityKey,
    string SemanticKey,
    string AdapterVersion,
    string ParserVersion,
    string FingerprintAlgorithm,
    string? StructuralFingerprint,
    string Outcome,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    string? ErrorCode,
    DateTimeOffset RepresentationObservedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long? SourceVersion,
    long? SourceLastUpdated);

public sealed record SourceMeasurementScheduleDecision(
    FetchId FetchId,
    EndpointId EndpointId,
    string SourceKey,
    string ShardKey,
    string Environment,
    string CapabilityKey,
    string SemanticKey,
    string PolicyVersion,
    long EffectiveCadenceMs,
    bool EndpointActive,
    bool ProbeSelected,
    DateTimeOffset? SourceCacheEligibleAt,
    DateTimeOffset? NextTargetAt,
    DateTimeOffset? RetryEligibleAt,
    CollectionJobId? SuccessorJobId,
    DateTimeOffset? SuccessorAvailableAt,
    DateTimeOffset CreatedAt);

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

    IAsyncEnumerable<SourceMeasurementParseRun> ReadParseRunsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SourceMeasurementScheduleDecision> ReadScheduleDecisionsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        CancellationToken cancellationToken);
}
