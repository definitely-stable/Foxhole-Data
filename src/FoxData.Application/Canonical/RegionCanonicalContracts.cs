using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed record WarSourceContextDescriptor(
    FetchId ValidationFetchId,
    FetchId? RepresentationFetchId,
    SourceParseRunId? SourceParseRunId,
    DateTimeOffset RetrievedAt,
    int? StatusCode);

public sealed record WarContextDescriptor(
    WarId WarId,
    ShardId ShardId,
    string SourceWarId,
    DateTimeOffset WarObservedAt);

public sealed record RegionDescriptor(
    RegionId Id,
    string CanonicalKey,
    string DisplayName,
    DateTimeOffset CreatedAt);

public sealed record WarRegionDescriptor(
    WarRegionId Id,
    WarId WarId,
    RegionId RegionId,
    string SourceMapName,
    int? SourceRegionId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset CreatedAt);

public sealed record RegionMembershipCandidate(
    string CanonicalKey,
    string DisplayName,
    string SourceMapName,
    int? SourceRegionId = null);

public sealed record RegionMembershipDescriptor(
    RegionDescriptor Region,
    WarRegionDescriptor Membership);

public sealed record RegionCanonicalWrite(
    SourceParseRunId SourceParseRunId,
    string NormalizerVersion,
    string CapabilityKey,
    DateTimeOffset NormalizationStartedAt,
    DateTimeOffset NormalizationCompletedAt,
    ShardId ShardId,
    FetchId RepresentationFetchId,
    WarId WarId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<RegionMembershipCandidate> Memberships);

public sealed record RegionCanonicalResult(
    NormalizationRunDescriptor NormalizationRun,
    WarContextDescriptor WarContext,
    IReadOnlyList<RegionMembershipDescriptor> Memberships);

public interface IWarContextReader
{
    Task<WarSourceContextDescriptor?> GetAtOrBeforeAsync(
        ShardId shardId,
        DateTimeOffset observedAt,
        string capabilityKey,
        string semanticKey,
        string parserVersion,
        CancellationToken cancellationToken);
}

public interface IRegionCanonicalStore
{
    Task<RegionCanonicalResult> RecordAcceptedAsync(
        RegionCanonicalWrite write,
        CancellationToken cancellationToken);
}
