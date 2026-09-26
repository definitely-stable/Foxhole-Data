namespace FoxData.Infrastructure.Persistence;

internal sealed class NormalizationRunRow
{
    public Guid Id { get; set; }
    public Guid SourceParseRunId { get; set; }
    public required string NormalizerVersion { get; set; }
    public required string Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class WarRow
{
    public Guid Id { get; set; }
    public Guid ShardId { get; set; }
    public required string SourceWarId { get; set; }
    public int? WarNumber { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RegionRow
{
    public Guid Id { get; set; }
    public required string CanonicalKey { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class WarRegionRow
{
    public Guid Id { get; set; }
    public Guid WarId { get; set; }
    public Guid RegionId { get; set; }
    public required string SourceMapName { get; set; }
    public int? SourceRegionId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class WarObservationRow
{
    public Guid Id { get; set; }
    public Guid WarId { get; set; }
    public Guid NormalizationRunId { get; set; }
    public Guid RepresentationFetchId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public int? WarNumber { get; set; }
    public string? Winner { get; set; }
    public DateTimeOffset? ConquestStartTime { get; set; }
    public DateTimeOffset? ConquestEndTime { get; set; }
    public DateTimeOffset? ResistanceStartTime { get; set; }
    public DateTimeOffset? ScheduledConquestEndTime { get; set; }
    public int? RequiredVictoryTowns { get; set; }
    public int? ShortRequiredVictoryTowns { get; set; }
}

internal sealed class WarReportObservationRow
{
    public Guid Id { get; set; }
    public Guid WarRegionId { get; set; }
    public Guid NormalizationRunId { get; set; }
    public Guid RepresentationFetchId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public long? TotalEnlistments { get; set; }
    public long? ColonialCasualties { get; set; }
    public long? WardenCasualties { get; set; }
    public int? DayOfWar { get; set; }
}
