using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public enum CoverageState
{
    Observed,
    SourceNotModified,
    SourceUnavailable,
    CollectorUnavailable,
    Rejected,
    Unknown,
}

public sealed record CoverageAttemptEvidence(
    IngestionAttemptId AttemptId,
    CollectionJobId CollectionJobId,
    EndpointId EndpointId,
    ShardId ShardId,
    string SourceKey,
    string Environment,
    string ShardKey,
    string CapabilityKey,
    string SemanticKey,
    string AttemptState,
    string? AttemptOutcomeCode,
    string? AttemptErrorCode,
    DateTimeOffset AttemptStartedAt,
    DateTimeOffset? AttemptCompletedAt,
    FetchId? ValidationFetchId,
    FetchId? RepresentationFetchId,
    DateTimeOffset? RetrievedAt,
    int? StatusCode,
    string? BodyErrorCode,
    string? ContentEncoding,
    byte[]? RepresentationBody,
    SourceParseRunId? SourceParseRunId,
    string? ParseOutcome,
    string? ParseErrorCode);

public sealed record CoverageObservationWrite(
    EndpointId EndpointId,
    CollectionJobId CollectionJobId,
    IngestionAttemptId AttemptId,
    FetchId? ValidationFetchId,
    FetchId? RepresentationFetchId,
    SourceParseRunId? SourceParseRunId,
    CoverageState State,
    DateTimeOffset BoundaryAt,
    string? DetailCode);

public sealed record CoverageObservationDescriptor(
    CoverageObservationId Id,
    EndpointId EndpointId,
    CollectionJobId CollectionJobId,
    IngestionAttemptId AttemptId,
    FetchId? ValidationFetchId,
    FetchId? RepresentationFetchId,
    SourceParseRunId? SourceParseRunId,
    CoverageState State,
    DateTimeOffset BoundaryAt,
    string? DetailCode,
    DateTimeOffset RecordedAt);

public sealed record CoverageContinuityCandidate(
    CoverageObservationDescriptor Coverage,
    ShardId ShardId,
    string SourceKey,
    string ShardKey,
    string CapabilityKey,
    string SemanticKey,
    byte[] RepresentationBody,
    string? ContentEncoding,
    string ParserVersion,
    string FingerprintAlgorithm,
    string? StructuralFingerprint,
    string ParseOutcome,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    long? DecodedByteLength);

public sealed record CoverageReprocessingRunDescriptor(
    CoverageReprocessingRunId Id,
    CoverageObservationId CoverageObservationId,
    string ProcessorVersion,
    string Outcome,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record CoverageContinuityWrite(
    CoverageObservationId CoverageObservationId,
    string ProcessorVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ShardId ShardId,
    WarId WarId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<RegionMembershipCandidate> Memberships);

public sealed record CoverageContinuityResult(
    CoverageReprocessingRunDescriptor Run,
    WarContextDescriptor WarContext,
    IReadOnlyList<RegionMembershipDescriptor> Memberships);

public sealed record CanonicalReprocessingCandidate(
    SourceParseRunId SourceParseRunId,
    string CapabilityKey,
    DateTimeOffset RetrievedAt);

public interface ICoverageStore
{
    Task<IReadOnlyList<CoverageAttemptEvidence>> GetUncoveredAttemptsAsync(
        string sourceKey,
        string parserVersion,
        int batchSize,
        CancellationToken cancellationToken);

    Task<CoverageObservationDescriptor> RecordAsync(
        CoverageObservationWrite write,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CoverageContinuityCandidate>> GetPendingMapContinuityAsync(
        string sourceKey,
        string processorVersion,
        int batchSize,
        CancellationToken cancellationToken);

    Task<CoverageContinuityResult> RecordMapContinuityAsync(
        CoverageContinuityWrite write,
        CancellationToken cancellationToken);

    Task<CoverageReprocessingRunDescriptor> RecordMapContinuityRejectedAsync(
        CoverageObservationId coverageObservationId,
        string processorVersion,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string errorCode,
        CancellationToken cancellationToken);

    Task<bool> IsMapContinuityAppliedAsync(
        FetchId validationFetchId,
        string processorVersion,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CanonicalReprocessingCandidate>>
        GetPendingCanonicalReprocessingAsync(
            string sourceKey,
            string parserVersion,
            string warNormalizerVersion,
            string regionNormalizerVersion,
            string warReportNormalizerVersion,
            int batchSize,
            CancellationToken cancellationToken);
}
