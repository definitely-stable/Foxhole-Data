using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Evidence;

public interface IEvidenceKernelStore
{
    Task<CaptureResult> CaptureSourceResponseAsync(
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
        CancellationToken cancellationToken);

    Task<FetchDescriptor?> GetFetchByAttemptAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken);

    Task<FetchDescriptor?> GetFetchAsync(
        FetchId fetchId,
        CancellationToken cancellationToken);

    Task<PayloadDescriptor?> GetPayloadAsync(
        PayloadId payloadId,
        CancellationToken cancellationToken);
}
