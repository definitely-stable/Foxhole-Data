namespace FoxData.Infrastructure.Persistence;

internal sealed class SourceScheduleDecisionRow
{
    public Guid FetchId { get; set; }
    public Guid EndpointId { get; set; }
    public required string PolicyVersion { get; set; }
    public long EffectiveCadenceMs { get; set; }
    public bool EndpointActive { get; set; }
    public bool ProbeSelected { get; set; }
    public DateTimeOffset? SourceCacheEligibleAt { get; set; }
    public DateTimeOffset? NextTargetAt { get; set; }
    public DateTimeOffset? RetryEligibleAt { get; set; }
    public Guid? SuccessorJobId { get; set; }
    public DateTimeOffset? SuccessorAvailableAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
