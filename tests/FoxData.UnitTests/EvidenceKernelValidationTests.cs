using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.UnitTests;

public sealed class EvidenceKernelValidationTests
{
    private readonly EvidenceKernel _kernel = new(new ThrowingStore());

    [Fact]
    public async Task PriorFetchCannotBeCombinedWithBody()
    {
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(
            () => _kernel.CaptureSourceResponseAsync(
                IngestionAttemptId.New(),
                EndpointId.New(),
                new LeaseGeneration(1),
                new FenceToken(1),
                new SourceResponseObservation(
                    now,
                    now,
                    now,
                    "fixture",
                    200,
                    "application/json",
                    null,
                    2,
                    null,
                    null,
                    null,
                    1),
                new byte[] { 1, 2 },
                FetchId.New(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidStatusCodeIsRejected()
    {
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _kernel.CaptureSourceResponseAsync(
                IngestionAttemptId.New(),
                EndpointId.New(),
                new LeaseGeneration(1),
                new FenceToken(1),
                new SourceResponseObservation(
                    now,
                    now,
                    now,
                    "fixture",
                    42,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1),
                body: null,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PayloadLimitRejectsOversizedBodyBeforeStoreInvocation()
    {
        var kernel = new EvidenceKernel(
            new ThrowingStore(),
            new EvidenceKernelLimits(maxPayloadBytes: 2));
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => kernel.CaptureSourceResponseAsync(
                IngestionAttemptId.New(),
                EndpointId.New(),
                new LeaseGeneration(1),
                new FenceToken(1),
                new SourceResponseObservation(
                    now,
                    now,
                    now,
                    "fixture",
                    200,
                    "application/octet-stream",
                    null,
                    3,
                    null,
                    null,
                    null,
                    1),
                new byte[] { 1, 2, 3 },
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EvidenceKernelLimits.MaximumMaxPayloadBytes + 1)]
    public void EvidenceKernelLimitsRejectInvalidBounds(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new EvidenceKernelLimits(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(IngestionRecoveryLimits.MaximumBatchSize + 1)]
    public void RecoveryLimitsRejectInvalidBounds(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IngestionRecoveryLimits(value));
    }

    private sealed class ThrowingStore : IEvidenceKernelStore
    {
        public Task<CaptureResult> CaptureSourceResponseAsync(
            FetchId proposedFetchId,
            PayloadId? proposedPayloadId,
            IngestionAttemptId attemptId,
            EndpointId endpointId,
            LeaseGeneration expectedLeaseGeneration,
            FenceToken expectedFenceToken,
            SourceResponseObservation observation,
            PayloadHash? payloadHash,
            ReadOnlyMemory<byte>? body,
            FetchId? priorFetchId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<FetchDescriptor?> GetFetchByAttemptAsync(
            IngestionAttemptId attemptId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<FetchDescriptor?> GetFetchAsync(
            FetchId fetchId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<PayloadDescriptor?> GetPayloadAsync(
            PayloadId payloadId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
