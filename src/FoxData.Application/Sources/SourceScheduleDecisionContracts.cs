using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public sealed record SourceScheduleDecisionDescriptor(
    FetchId FetchId,
    EndpointId EndpointId,
    string PolicyVersion,
    long EffectiveCadenceMs,
    bool EndpointActive,
    bool ProbeSelected,
    DateTimeOffset? SourceCacheEligibleAt,
    DateTimeOffset? NextTargetAt,
    DateTimeOffset? RetryEligibleAt,
    CollectionJobId? SuccessorJobId,
    DateTimeOffset? SuccessorAvailableAt,
    DateTimeOffset CreatedAt);

public sealed record SourceScheduleDecisionWrite(
    FetchId FetchId,
    EndpointId EndpointId,
    string PolicyVersion,
    long EffectiveCadenceMs,
    bool EndpointActive,
    bool ProbeSelected,
    DateTimeOffset? SourceCacheEligibleAt,
    DateTimeOffset? NextTargetAt,
    DateTimeOffset? RetryEligibleAt,
    CollectionJobId? SuccessorJobId,
    DateTimeOffset? SuccessorAvailableAt);

public interface ISourceScheduleDecisionStore
{
    Task<SourceScheduleDecisionDescriptor?> GetAsync(
        FetchId fetchId,
        CancellationToken cancellationToken);

    Task<SourceScheduleDecisionDescriptor> RecordAsync(
        SourceScheduleDecisionWrite decision,
        CancellationToken cancellationToken);
}
