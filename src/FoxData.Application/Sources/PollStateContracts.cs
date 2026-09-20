using FoxData.Core.Evidence;
using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public sealed record EndpointPollStateDescriptor(
    EndpointId EndpointId,
    FetchId? LastProcessedFetchId,
    FetchId? LatestValidationFetchId,
    FetchId? RepresentationFetchId,
    string? ValidatorEtag,
    DateTimeOffset? SourceCacheEligibleAt,
    DateTimeOffset? NextTargetAt,
    DateTimeOffset? RetryEligibleAt,
    DateTimeOffset? LastHttpResponseAt,
    DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures,
    string PolicyVersion,
    DateTimeOffset UpdatedAt);

public sealed record EndpointPollStateWrite(
    EndpointId EndpointId,
    FetchId? LastProcessedFetchId,
    FetchId? LatestValidationFetchId,
    FetchId? RepresentationFetchId,
    string? ValidatorEtag,
    DateTimeOffset? SourceCacheEligibleAt,
    DateTimeOffset? NextTargetAt,
    DateTimeOffset? RetryEligibleAt,
    DateTimeOffset? LastHttpResponseAt,
    DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures,
    string PolicyVersion);

public interface IEndpointPollStateStore
{
    Task<EndpointPollStateDescriptor?> GetAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken);

    Task<EndpointPollStateDescriptor> PutAsync(
        EndpointPollStateWrite state,
        CancellationToken cancellationToken);
}
