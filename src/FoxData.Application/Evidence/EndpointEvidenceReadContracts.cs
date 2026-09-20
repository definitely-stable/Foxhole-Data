using FoxData.Core.Evidence;
using FoxData.Core.Sources;

namespace FoxData.Application.Evidence;

public sealed record EndpointEvidenceSnapshot(
    EndpointId EndpointId,
    FetchDescriptor CurrentFetch,
    FetchDescriptor? RepresentationFetch,
    PayloadDescriptor? RepresentationPayload);

public interface IEndpointEvidenceReader
{
    Task<EndpointEvidenceSnapshot?> GetCurrentAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken);
}
