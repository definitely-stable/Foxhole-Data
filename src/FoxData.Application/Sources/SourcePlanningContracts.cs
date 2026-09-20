using FoxData.Core.Sources;

namespace FoxData.Application.Sources;

public interface ISourcePlanningReader
{
    Task<IReadOnlyList<EndpointId>> ListPendingCurrentEndpointsAsync(
        string sourceKey,
        int maximumCount,
        CancellationToken cancellationToken);
}
