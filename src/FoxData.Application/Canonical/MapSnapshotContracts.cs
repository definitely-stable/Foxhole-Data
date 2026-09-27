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
    IReadOnlyList<MapItemOccurrenceCandidate> Items,
    IReadOnlyList<MapTextOccurrenceCandidate> TextItems);

public sealed record MapSnapshotResult(
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotDescriptor Snapshot,
    IReadOnlyList<MapItemOccurrenceDescriptor> Items,
    IReadOnlyList<MapTextOccurrenceDescriptor> TextItems);

public interface IMapSnapshotStore
{
    Task<MapSnapshotResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken);

    Task<MapSnapshotResult> RecordAcceptedAsync(
        MapSnapshotWrite write,
        CancellationToken cancellationToken);
}
