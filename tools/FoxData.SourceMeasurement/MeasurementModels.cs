using FoxData.Infrastructure.Sources;
using FoxData.Sources.WarApi;

namespace FoxData.SourceMeasurement;

internal sealed record MeasurementProbeManifest(
    string Version,
    string RunId,
    IReadOnlyList<string> ShardKeys,
    int MaxMapsPerShard,
    int TargetCadenceSeconds);

internal sealed record MeasurementPhaseObservation(
    string Kind,
    string Scope,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    int EvidenceCount);

internal sealed record MeasurementManifest(
    string MeasurementVersion,
    string RunId,
    string SourceKey,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    DateTimeOffset GeneratedAt,
    string RepositorySha,
    string ObserverRegion,
    string AdapterVersion,
    string ParserVersion,
    string CachePolicyVersion,
    string BackoffPolicyVersion,
    string PollPolicyVersion,
    string CollectionProfileVersion,
    IReadOnlyList<string> Shards,
    MeasurementProbeManifest? Probe,
    IReadOnlyDictionary<string, int> SchedulingPolicyCounts,
    IReadOnlyList<MeasurementPhaseObservation> ObservedPhases,
    IReadOnlyList<string> Limitations);

internal sealed record MeasurementPercentiles(
    double? P50,
    double? P90,
    double? P95,
    double? P99,
    double? Max);

internal sealed record MeasurementCacheSummary(
    int CacheControlPresentCount,
    int CacheControlMalformedCount,
    int NoCacheCount,
    int NoStoreCount,
    int ExpiresPresentCount,
    int SourceDatePresentCount,
    int SourceAgePresentCount,
    int RetryAfterPresentCount,
    int RetryAfterMalformedCount,
    MeasurementPercentiles MaxAgeSeconds,
    MeasurementPercentiles SharedMaxAgeSeconds,
    MeasurementPercentiles ExpiresLifetimeSeconds,
    MeasurementPercentiles FreshnessLifetimeSeconds,
    MeasurementPercentiles SourceCacheDelaySeconds,
    MeasurementPercentiles SourceAgeSeconds,
    MeasurementPercentiles RetryAfterDelaySeconds);

internal sealed record MeasurementPayloadDecodeSummary(
    int ParseRunCount,
    int DecodedCount,
    int MissingDecodedLengthCount,
    MeasurementPercentiles DecodedSizeBytes,
    MeasurementPercentiles ExpansionRatio);

internal sealed record MeasurementEtagSummary(
    int PresentCount,
    int StrongCount,
    int WeakCount,
    int UnusableCount,
    int SameEtagDifferentPayloadCount,
    int DifferentEtagSamePayloadCount);

internal sealed record MeasurementVolumeSummary(
    double WindowDays,
    double FetchRowsPerDay,
    double UniquePayloadRowsPerDay,
    double ParseRunsPerDay,
    double ScheduleDecisionsPerDay);

internal sealed record MeasurementExecutorCapabilityLoad(
    string ShardKey,
    string CapabilityKey,
    int EndpointCount,
    double RequiredSerialServiceLoad);

internal sealed record MeasurementExecutorCapacitySummary(
    int ExecutorConcurrency,
    int ModeledEndpointCount,
    double RequiredSerialServiceLoad,
    double RemainingSerialHeadroom,
    int MinimumModeledConcurrency,
    IReadOnlyList<MeasurementExecutorCapabilityLoad> Groups);

internal sealed record MeasurementAttemptSummary(
    int AttemptCount,
    int ExchangeAuthorizedCount,
    int PreExchangeFailureCount,
    int UncertainExchangeCount,
    int CapturedLateCount,
    int NegativeLogicalStartLagCount,
    MeasurementPercentiles LogicalStartLagSeconds,
    IReadOnlyDictionary<string, int> StateCounts,
    IReadOnlyDictionary<string, int> OutcomeCounts,
    IReadOnlyDictionary<string, int> ErrorClassCounts);

internal sealed record MeasurementSchedulingSummary(
    int WindowDecisionCount,
    int MissingWindowDecisionCount,
    int ProbeSelectedDecisionCount,
    int SuccessorDecisionCount,
    int ProbeAttributedFetchCount,
    int BaselineAttributedFetchCount,
    int UnattributedFetchCount,
    MeasurementPercentiles EffectiveCadenceSeconds,
    MeasurementPercentiles SourceCacheDelaySeconds,
    MeasurementPercentiles RetryDelaySeconds,
    MeasurementPercentiles SuccessorDelaySeconds,
    MeasurementPercentiles SuccessorExtensionBeyondCadenceSeconds);

internal sealed record MeasurementDownsampleSeries(
    string ShardKey,
    string EndpointKey,
    string CapabilityKey,
    int ProbeAttributedFetchCount,
    IReadOnlyList<WarApiDownsampleSummary> Candidates);

internal sealed record MeasurementStorageGrowthRelation(
    string SchemaName,
    string RelationName,
    long DeltaBytes,
    double BytesPerDay,
    double Projected30DayBytes,
    double Projected365DayBytes);

internal sealed record MeasurementStorageGrowth(
    DateTimeOffset BeforeCapturedAt,
    DateTimeOffset AfterCapturedAt,
    long DatabaseDeltaBytes,
    double DatabaseBytesPerDay,
    double Projected30DayBytes,
    double Projected365DayBytes,
    IReadOnlyList<MeasurementStorageGrowthRelation> Relations);

internal sealed record MeasurementSummary(
    string RunId,
    string SourceKey,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    int FetchCount,
    int AttemptCount,
    int ParseRunCount,
    int EndpointCount,
    int BodyBearingFetchCount,
    int UniquePayloadCount,
    double? PayloadDeduplicationRatio,
    int DeclaredLengthMismatchCount,
    int EtagPresentCount,
    IReadOnlyDictionary<string, int> StatusCounts,
    IReadOnlyDictionary<string, int> ContentEncodingCounts,
    IReadOnlyDictionary<string, int> BodyErrorCounts,
    MeasurementEtagSummary Etag,
    MeasurementPayloadDecodeSummary PayloadDecode,
    IReadOnlyDictionary<string, MeasurementPercentiles> DecodeAndParseDurationByCapabilityMs,
    IReadOnlyDictionary<string, MeasurementPercentiles> LatencyByResponseClassMs,
    MeasurementVolumeSummary Volume,
    MeasurementExecutorCapacitySummary ExecutorCapacity,
    MeasurementCacheSummary Cache,
    MeasurementAttemptSummary Attempts,
    MeasurementSchedulingSummary Scheduling,
    IReadOnlyList<MeasurementDownsampleSeries> Downsampling,
    MeasurementStorageGrowth? StorageGrowth,
    WarApiMeasurementReport WarApi);

internal sealed record AnalyzeOptions(
    string RunId,
    DateTimeOffset StartInclusive,
    DateTimeOffset EndExclusive,
    string OutputDirectory,
    string RepositorySha,
    string ObserverRegion,
    string CollectionProfileVersion,
    IReadOnlyList<string> ProbeShardKeys,
    int? ProbeMaxMapsPerShard,
    int? ProbeTargetCadenceSeconds);

internal sealed record MeasurementValidationResult(
    bool EvidenceComplete,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

internal sealed record ValidationOptions(
    string OutputDirectory);

internal sealed record StorageOptions(
    string Label,
    string OutputDirectory);
