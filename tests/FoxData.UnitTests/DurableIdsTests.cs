using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.UnitTests;

public sealed class DurableIdsTests
{
    [Fact]
    public void NewIdentifiersAreNonEmptyAndDistinct()
    {
        Guid[] values =
        [
            SourceId.New().Value,
            ShardId.New().Value,
            EndpointId.New().Value,
            CollectionJobId.New().Value,
            IngestionAttemptId.New().Value,
            WorkerInstanceId.New().Value,
            FetchId.New().Value,
            PayloadId.New().Value,
        ];

        Assert.All(values, value => Assert.NotEqual(Guid.Empty, value));
        Assert.Equal(values.Length, values.Distinct().Count());
    }
}
