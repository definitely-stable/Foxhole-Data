using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;

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

public interface IMapQualityStore
{
    Task<MapQualityResult?> GetByRunIdAsync(
        MapQualityRunId runId,
        CancellationToken cancellationToken);

    Task<MapQualityResult> RecordAsync(
        MapQualityWrite write,
        CancellationToken cancellationToken);
}
