using FoxData.Application.Evidence;
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
