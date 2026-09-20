using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Ingestion;

public interface IIngestionKernelStore
{
    Task<JobEnqueueResult> EnqueueAsync(
        CollectionJobId proposedId,
        EndpointId endpointId,
        string idempotencyKey,
        DateTimeOffset scheduledFor,
        DateTimeOffset availableAt,
        short priority,
        CancellationToken cancellationToken);

    Task<JobClaimResult> ClaimNextAsync(
        WorkerInstanceId workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<LeaseRenewalResult> RenewLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<LeaseReleaseResult> ReleaseLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken);

    Task<BeginAttemptResult> BeginAttemptAsync(
        IngestionAttemptId attemptId,
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken);

    Task<FenceAcquireResult> AcquireEndpointFenceAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken);

    Task<ExchangeAuthorizationResult> AuthorizeExchangeAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken);

    Task<CollectionJobDescriptor?> GetJobAsync(
        CollectionJobId jobId,
        CancellationToken cancellationToken);

    Task<IngestionAttemptDescriptor?> GetAttemptAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken);
}
