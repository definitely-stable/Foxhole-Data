using FoxData.Application.Ingestion;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.UnitTests;

public sealed class IngestionKernelValidationTests
{
    private readonly IngestionKernel _kernel = new(new ThrowingStore());

    [Fact]
    public async Task EnqueueRejectsWhitespacePaddedIdempotencyKey()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _kernel.EnqueueAsync(
                EndpointId.New(),
                " job ",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClaimRejectsNonPositiveLeaseDuration()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _kernel.ClaimNextAsync(
                WorkerInstanceId.New(),
                TimeSpan.Zero,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AttemptRequiresPositiveLeaseGeneration()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _kernel.BeginAttemptAsync(
                IngestionAttemptId.New(),
                CollectionJobId.New(),
                WorkerInstanceId.New(),
                LeaseGeneration.Zero,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeferralRejectsBlankDiagnosticCode()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _kernel.DeferBeforeExchangeAsync(
                IngestionAttemptId.New(),
                WorkerInstanceId.New(),
                new LeaseGeneration(1),
                DateTimeOffset.UtcNow,
                "network",
                " ",
                TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingStore : IIngestionKernelStore
    {
        public Task<JobEnqueueResult> EnqueueAsync(
            CollectionJobId proposedId,
            EndpointId endpointId,
            string idempotencyKey,
            DateTimeOffset scheduledFor,
            DateTimeOffset availableAt,
            short priority,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<JobClaimResult> ClaimNextAsync(
            WorkerInstanceId workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<LeaseRenewalResult> RenewLeaseAsync(
            CollectionJobId jobId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<LeaseReleaseResult> ReleaseLeaseAsync(
            CollectionJobId jobId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<BeginAttemptResult> BeginAttemptAsync(
            IngestionAttemptId attemptId,
            CollectionJobId jobId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<FenceAcquireResult> AcquireEndpointFenceAsync(
            IngestionAttemptId attemptId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ExchangeAuthorizationResult> AuthorizeExchangeAsync(
            IngestionAttemptId attemptId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<AttemptDeferralResult> DeferBeforeExchangeAsync(
            IngestionAttemptId attemptId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            DateTimeOffset retryAvailableAt,
            string errorClass,
            string errorCode,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<AttemptDeferralResult> DeferUncertainExchangeAsync(
            IngestionAttemptId attemptId,
            WorkerInstanceId workerId,
            LeaseGeneration leaseGeneration,
            DateTimeOffset retryAvailableAt,
            string errorClass,
            string errorCode,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<CollectionJobDescriptor?> GetJobAsync(
            CollectionJobId jobId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<IngestionAttemptDescriptor?> GetAttemptAsync(
            IngestionAttemptId attemptId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
