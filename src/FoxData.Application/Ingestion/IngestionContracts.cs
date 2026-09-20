using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Ingestion;

public sealed record CollectionJobDescriptor(
    CollectionJobId Id,
    EndpointId EndpointId,
    string IdempotencyKey,
    DateTimeOffset ScheduledFor,
    DateTimeOffset AvailableAt,
    short Priority,
    CollectionJobState State,
    WorkerInstanceId? LeaseOwnerId,
    LeaseGeneration LeaseGeneration,
    DateTimeOffset? LeaseExpiresAt,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record IngestionAttemptDescriptor(
    IngestionAttemptId Id,
    CollectionJobId JobId,
    int AttemptNumber,
    LeaseGeneration LeaseGeneration,
    FenceToken? FenceToken,
    IngestionAttemptState State,
    string? OutcomeCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExchangeAuthorizedAt,
    DateTimeOffset? RawDurableAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? RecoveredAt,
    DateTimeOffset? SupersededAt,
    string? ErrorClass,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum JobEnqueueStatus
{
    Created,
    Existing,
    Conflict,
}

public sealed record JobEnqueueResult(
    JobEnqueueStatus Status,
    CollectionJobDescriptor Job);

public sealed record JobClaimResult(CollectionJobDescriptor? Job)
{
    public bool Claimed => Job is not null;

    public static JobClaimResult None { get; } = new((CollectionJobDescriptor?)null);
}

public enum LeaseRenewalStatus
{
    Renewed,
    Lost,
}

public sealed record LeaseRenewalResult(
    LeaseRenewalStatus Status,
    CollectionJobDescriptor? Job);

public enum LeaseReleaseStatus
{
    Released,
    Lost,
    InvalidState,
}

public sealed record LeaseReleaseResult(
    LeaseReleaseStatus Status,
    CollectionJobDescriptor? Job);

public enum BeginAttemptStatus
{
    Started,
    Existing,
    LeaseLost,
    ActiveAttemptExists,
}

public sealed record BeginAttemptResult(
    BeginAttemptStatus Status,
    IngestionAttemptDescriptor? Attempt);

public enum FenceAcquireStatus
{
    AcquiredNow,
    AlreadyAcquired,
    LeaseLost,
    InvalidAttempt,
    InvalidState,
    StaleFence,
}

public sealed record FenceAcquireResult(
    FenceAcquireStatus Status,
    IngestionAttemptDescriptor? Attempt)
{
    public bool IsCurrent =>
        Status is FenceAcquireStatus.AcquiredNow or FenceAcquireStatus.AlreadyAcquired;
}

public enum ExchangeAuthorizationStatus
{
    AuthorizedNow,
    AlreadyAuthorized,
    LeaseLost,
    InvalidAttempt,
    InvalidState,
    StaleFence,
}

public sealed record ExchangeAuthorizationResult(
    ExchangeAuthorizationStatus Status,
    IngestionAttemptDescriptor? Attempt)
{
    public bool MayPerformExchange => Status is ExchangeAuthorizationStatus.AuthorizedNow;
}


public enum AttemptDeferralStatus
{
    DeferredNow,
    AlreadyDeferred,
    LeaseLost,
    InvalidAttempt,
    InvalidState,
}

public sealed record AttemptDeferralResult(
    AttemptDeferralStatus Status,
    IngestionAttemptDescriptor? Attempt,
    CollectionJobDescriptor? Job)
{
    public bool Deferred =>
        Status is AttemptDeferralStatus.DeferredNow or AttemptDeferralStatus.AlreadyDeferred;
}
