using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public enum MapQualityDecision
{
    Accepted,
    Suspect,
    Quarantined,
}

public enum MapQualityFindingEffect
{
    Informational,
    Suspect,
    Quarantined,
}

public sealed record MapQualityFindingCandidate(
    string RuleKey,
    string RuleVersion,
    string ConfigurationVersion,
    MapQualityFindingEffect Effect,
    MapItemOccurrenceId? MapItemOccurrenceId,
    MapTextOccurrenceId? MapTextOccurrenceId,
    string? DetailCode,
    string InputMetricsJson);

public sealed record MapQualityRunDescriptor(
    MapQualityRunId Id,
    MapSnapshotId MapSnapshotId,
    WarRegionId WarRegionId,
    FetchId ValidationFetchId,
    string TaxonomyVersion,
    string QualityPolicyVersion,
    MapObservationId? BaselineMapObservationId,
    MapQualityDecision Decision,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record MapQualityFindingDescriptor(
    MapQualityFindingId Id,
    MapQualityRunId QualityRunId,
    MapSnapshotId MapSnapshotId,
    string RuleKey,
    string RuleVersion,
    string ConfigurationVersion,
    MapQualityFindingEffect Effect,
    MapItemOccurrenceId? MapItemOccurrenceId,
    MapTextOccurrenceId? MapTextOccurrenceId,
    string? DetailCode,
    string InputMetricsJson,
    DateTimeOffset CreatedAt);

public sealed record MapObservationDescriptor(
    MapObservationId Id,
    WarRegionId WarRegionId,
    MapSnapshotId MapSnapshotId,
    MapQualityRunId QualityRunId,
    FetchId ValidationFetchId,
    MapSnapshotKind Kind,
    DateTimeOffset ObservedAt,
    DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset RecordedAt);

public sealed record MapQualityWrite(
    MapSnapshotId MapSnapshotId,
    WarRegionId WarRegionId,
    FetchId ValidationFetchId,
    string TaxonomyVersion,
    string QualityPolicyVersion,
    MapObservationId? BaselineMapObservationId,
    MapQualityDecision Decision,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<MapQualityFindingCandidate> Findings);

public sealed record MapQualityResult(
    MapQualityRunDescriptor Run,
    IReadOnlyList<MapQualityFindingDescriptor> Findings,
    MapObservationDescriptor? Observation,
    WarRegionDescriptor WarRegion);

public enum MapQualityOrderingStatus
{
    Ready,
    Deferred,
}

public sealed record MapQualityOrderingPlan(
    MapQualityOrderingStatus Status,
    string? DeferredReason,
    DateTimeOffset ObservedAt,
    MapObservationDescriptor? Baseline,
    string? CurrentStructuralFingerprint,
    string? BaselineStructuralFingerprint)
{
    public static MapQualityOrderingPlan Deferred(
        DateTimeOffset observedAt,
        string reason) =>
        new(
            MapQualityOrderingStatus.Deferred,
            reason,
            observedAt,
            null,
            null,
            null);

    public static MapQualityOrderingPlan Ready(
        DateTimeOffset observedAt,
        MapObservationDescriptor? baseline,
        string? currentStructuralFingerprint,
        string? baselineStructuralFingerprint) =>
        new(
            MapQualityOrderingStatus.Ready,
            null,
            observedAt,
            baseline,
            currentStructuralFingerprint,
            baselineStructuralFingerprint);
}

public static class MapQualityDeferredReasons
{
    public const string LaterQualityAlreadyTerminal =
        "later_quality_already_terminal";

    public const string CrossWarValidationBinding =
        "cross_war_validation_binding";
}

public sealed class MapQualityOrderingDeferredException(
    string reason)
    : Exception(
        $"Map quality ordering is deferred: {reason}.")
{
    public string Reason { get; } = reason;
}

public interface IMapQualityOrderingReader
{
    Task<MapQualityOrderingPlan> GetPlanAsync(
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken);
}

public enum MapQualityGapValidationKind
{
    BodyBearing200,
    NotModified304,
}

public sealed record MapQualityGapCandidate(
    NormalizationRunId NormalizationRunId,
    ShardId ShardId,
    string CapabilityKey,
    MapQualityGapValidationKind ValidationKind,
    DateTimeOffset RepresentationObservedAt,
    FetchId ValidationFetchId,
    DateTimeOffset ObservedAt);

public interface IMapQualityGapReader
{
    Task<IReadOnlyList<MapQualityGapCandidate>> GetPendingAsync(
        string sourceKey,
        IReadOnlyList<CoverageCapabilityPlan> capabilities,
        string taxonomyVersion,
        string qualityPolicyVersion,
        DateTimeOffset? afterObservedAt,
        FetchId? afterValidationFetchId,
        int batchSize,
        CancellationToken cancellationToken);
}

public interface IMapQualityStore
{
    Task<MapQualityResult?> GetAsync(
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken);

    Task<MapQualityResult?> GetByRunIdAsync(
        MapQualityRunId runId,
        CancellationToken cancellationToken);

    Task<MapQualityResult> RecordAsync(
        MapQualityWrite write,
        CancellationToken cancellationToken);
}
