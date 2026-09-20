using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Ingestion;

public sealed class IngestionKernel(IIngestionKernelStore store)
{
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(24);

    public Task<JobEnqueueResult> EnqueueAsync(
        EndpointId endpointId,
        string idempotencyKey,
        DateTimeOffset scheduledFor,
        DateTimeOffset availableAt,
        short priority = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(endpointId.Value, nameof(endpointId));
        ValidateIdempotencyKey(idempotencyKey);

        if (availableAt < scheduledFor)
        {
            throw new ArgumentException(
                "AvailableAt must not be earlier than ScheduledFor.",
                nameof(availableAt));
        }

        return store.EnqueueAsync(
            CollectionJobId.New(),
            endpointId,
            idempotencyKey,
            scheduledFor,
            availableAt,
            priority,
            cancellationToken);
    }

    public Task<JobClaimResult> ClaimNextAsync(
        WorkerInstanceId workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        ValidateLeaseDuration(leaseDuration);

        return store.ClaimNextAsync(workerId, leaseDuration, cancellationToken);
    }

    public Task<LeaseRenewalResult> RenewLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId.Value, nameof(jobId));
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        EnsurePositive(leaseGeneration, nameof(leaseGeneration));
        ValidateLeaseDuration(leaseDuration);

        return store.RenewLeaseAsync(
            jobId,
            workerId,
            leaseGeneration,
            leaseDuration,
            cancellationToken);
    }

    public Task<LeaseReleaseResult> ReleaseLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId.Value, nameof(jobId));
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        EnsurePositive(leaseGeneration, nameof(leaseGeneration));

        return store.ReleaseLeaseAsync(
            jobId,
            workerId,
            leaseGeneration,
            cancellationToken);
    }

    public Task<BeginAttemptResult> BeginAttemptAsync(
        IngestionAttemptId attemptId,
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        EnsureNonEmpty(jobId.Value, nameof(jobId));
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        EnsurePositive(leaseGeneration, nameof(leaseGeneration));

        return store.BeginAttemptAsync(
            attemptId,
            jobId,
            workerId,
            leaseGeneration,
            cancellationToken);
    }

    public Task<FenceAcquireResult> AcquireEndpointFenceAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        EnsurePositive(leaseGeneration, nameof(leaseGeneration));

        return store.AcquireEndpointFenceAsync(
            attemptId,
            workerId,
            leaseGeneration,
            cancellationToken);
    }

    public Task<ExchangeAuthorizationResult> AuthorizeExchangeAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        EnsureNonEmpty(workerId.Value, nameof(workerId));
        EnsurePositive(leaseGeneration, nameof(leaseGeneration));

        return store.AuthorizeExchangeAsync(
            attemptId,
            workerId,
            leaseGeneration,
            cancellationToken);
    }

    public Task<CollectionJobDescriptor?> GetJobAsync(
        CollectionJobId jobId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(jobId.Value, nameof(jobId));
        return store.GetJobAsync(jobId, cancellationToken);
    }

    public Task<IngestionAttemptDescriptor?> GetAttemptAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        return store.GetAttemptAsync(attemptId, cancellationToken);
    }

    private static void ValidateIdempotencyKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Idempotency key must be non-empty, already trimmed, and at most 256 characters.",
                nameof(value));
        }
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                $"Lease duration must be greater than zero and no more than {MaximumLeaseDuration}.");
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private static void EnsurePositive(LeaseGeneration generation, string parameterName)
    {
        if (generation.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                generation.Value,
                "Lease generation must be positive for an owned job.");
        }
    }
}
