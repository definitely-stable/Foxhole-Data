using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed record CanonicalWarReportSnapshot(
    long? TotalEnlistments,
    long? ColonialCasualties,
    long? WardenCasualties,
    int? DayOfWar);

public sealed record WarReportObservationDescriptor(
    WarReportObservationId Id,
    WarRegionId WarRegionId,
    NormalizationRunId NormalizationRunId,
    FetchId RepresentationFetchId,
    DateTimeOffset ObservedAt,
    DateTimeOffset RecordedAt,
    long? TotalEnlistments,
    long? ColonialCasualties,
    long? WardenCasualties,
    int? DayOfWar);

public sealed record WarReportCanonicalWrite(
    SourceParseRunId SourceParseRunId,
    string NormalizerVersion,
    string CapabilityKey,
    DateTimeOffset NormalizationStartedAt,
    DateTimeOffset NormalizationCompletedAt,
    ShardId ShardId,
    FetchId RepresentationFetchId,
    WarId WarId,
    WarRegionId WarRegionId,
    string SourceMapName,
    DateTimeOffset ObservedAt,
    CanonicalWarReportSnapshot Snapshot);

public sealed record WarReportCanonicalResult(
    WarRegionDescriptor WarRegion,
    WarReportObservationDescriptor Observation,
    NormalizationRunDescriptor NormalizationRun);

public interface IWarRegionReader
{
    Task<WarRegionDescriptor?> GetAsync(
        WarId warId,
        string sourceMapName,
        CancellationToken cancellationToken);
}

public interface IWarReportCanonicalStore
{
    Task<WarReportCanonicalResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken);

    Task<WarReportCanonicalResult> RecordAcceptedAsync(
        WarReportCanonicalWrite write,
        CancellationToken cancellationToken);
}
