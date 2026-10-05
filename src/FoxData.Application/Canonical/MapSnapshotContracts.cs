using FoxData.Core.Evidence;

namespace FoxData.Application.Canonical;

public enum MapSnapshotKind
{
    Static,
    Dynamic,
}

public sealed record MapItemOccurrenceCandidate(
    int SourceOrdinal,
    string? RawTeamId,
    int? RawIconType,
    double? X,
    double? Y,
    int? RawFlags,
    int? RawViewDirection);

public sealed record MapTextOccurrenceCandidate(
    int SourceOrdinal,
    string? Text,
    double? X,
    double? Y,
    string? RawMapMarkerType);

public sealed record MapSnapshotDescriptor(
    MapSnapshotId Id,
    NormalizationRunId NormalizationRunId,
    SourceParseRunId SourceParseRunId,
    FetchId RepresentationFetchId,
    MapSnapshotKind Kind,
    string SourceMapName,
    int? SourceRegionId,
    int? SourceScorchedVictoryTowns,
    long? SourceVersion,
    long? SourceLastUpdatedMs,
    DateTimeOffset? SourceUpdatedAt,
    bool SourceMapItemsArrayPresent,
    bool SourceMapTextItemsArrayPresent,
    int ItemCount,
    int TextItemCount,
    DateTimeOffset RecordedAt);

public sealed record MapItemOccurrenceDescriptor(
    MapItemOccurrenceId Id,
    MapSnapshotId MapSnapshotId,
    int SourceOrdinal,
    string? RawTeamId,
    int? RawIconType,
    double? X,
    double? Y,
    int? RawFlags,
    int? RawViewDirection);

public sealed record MapTextOccurrenceDescriptor(
    MapTextOccurrenceId Id,
    MapSnapshotId MapSnapshotId,
    int SourceOrdinal,
    string? Text,
    double? X,
    double? Y,
    string? RawMapMarkerType);

public sealed record MapSnapshotWrite(
    SourceParseRunId SourceParseRunId,
    string NormalizerVersion,
    string CapabilityKey,
    string SemanticKey,
    DateTimeOffset NormalizationStartedAt,
    DateTimeOffset NormalizationCompletedAt,
    FetchId RepresentationFetchId,
    MapSnapshotKind Kind,
    string SourceMapName,
    int? SourceRegionId,
    int? SourceScorchedVictoryTowns,
    long? SourceVersion,
    long? SourceLastUpdatedMs,
    DateTimeOffset? SourceUpdatedAt,
    bool SourceMapItemsArrayPresent,
    bool SourceMapTextItemsArrayPresent,
    IReadOnlyList<MapItemOccurrenceCandidate> Items,
    IReadOnlyList<MapTextOccurrenceCandidate> TextItems);

public sealed record MapSnapshotResult(
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotDescriptor Snapshot,
    IReadOnlyList<MapItemOccurrenceDescriptor> Items,
    IReadOnlyList<MapTextOccurrenceDescriptor> TextItems);

public interface IMapSnapshotStore
{
    Task<MapSnapshotResult?> GetByIdAsync(
        MapSnapshotId mapSnapshotId,
        CancellationToken cancellationToken);

    Task<MapSnapshotResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken);

    Task<MapSnapshotResult> RecordAcceptedAsync(
        MapSnapshotWrite write,
        CancellationToken cancellationToken);
}
