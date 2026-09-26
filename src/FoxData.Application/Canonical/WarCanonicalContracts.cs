using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed record CanonicalWarSnapshot(
    string SourceWarId,
    int? WarNumber,
    string? Winner,
    DateTimeOffset? ConquestStartTime,
    DateTimeOffset? ConquestEndTime,
    DateTimeOffset? ResistanceStartTime,
    DateTimeOffset? ScheduledConquestEndTime,
    int? RequiredVictoryTowns,
    int? ShortRequiredVictoryTowns);

public sealed record WarDescriptor(
    WarId Id,
    ShardId ShardId,
    string SourceWarId,
    int? WarNumber,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset CreatedAt);

public sealed record WarObservationDescriptor(
    WarObservationId Id,
    WarId WarId,
    NormalizationRunId NormalizationRunId,
    FetchId RepresentationFetchId,
    DateTimeOffset ObservedAt,
    DateTimeOffset RecordedAt,
    int? WarNumber,
    string? Winner,
    DateTimeOffset? ConquestStartTime,
    DateTimeOffset? ConquestEndTime,
    DateTimeOffset? ResistanceStartTime,
    DateTimeOffset? ScheduledConquestEndTime,
    int? RequiredVictoryTowns,
    int? ShortRequiredVictoryTowns);

public sealed record WarCanonicalWrite(
    SourceParseRunId SourceParseRunId,
    NormalizationRunId NormalizationRunId,
    ShardId ShardId,
    FetchId RepresentationFetchId,
    DateTimeOffset ObservedAt,
    CanonicalWarSnapshot Snapshot);

public sealed record WarCanonicalResult(
    WarDescriptor War,
    WarObservationDescriptor Observation,
    NormalizationRunDescriptor NormalizationRun);

public interface IWarCanonicalStore
{
    Task<(WarDescriptor War, WarObservationDescriptor Observation)> RecordAsync(
        WarCanonicalWrite write,
        CancellationToken cancellationToken);
}
