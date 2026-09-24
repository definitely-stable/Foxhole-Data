using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using FoxData.SourceMeasurement;

namespace FoxData.IntegrationTests;

public sealed class SourceMeasurementBoundaryTests
{
    [Fact]
    public void DecisionForActiveFetchRemainsInMeasurementEvenIfCreatedOutsideWindow()
    {
        var fetchId = new FetchId(Guid.NewGuid());
        var decision = Decision(
            fetchId,
            successorJobId: null,
            createdAt: DateTimeOffset.Parse("2026-09-23T12:00:01+00:00"));

        var included = MeasurementRunner.ScheduleDecisionBelongsToActiveEvidence(
            decision,
            new HashSet<Guid> { fetchId.Value },
            new HashSet<Guid>());

        Assert.True(included);
    }

    [Fact]
    public void DecisionLeadingIntoActiveSuccessorRemainsInMeasurement()
    {
        var successorJobId = new CollectionJobId(Guid.NewGuid());
        var decision = Decision(
            new FetchId(Guid.NewGuid()),
            successorJobId,
            DateTimeOffset.Parse("2026-09-23T11:59:59+00:00"));

        var included = MeasurementRunner.ScheduleDecisionBelongsToActiveEvidence(
            decision,
            new HashSet<Guid>(),
            new HashSet<Guid> { successorJobId.Value });

        Assert.True(included);
    }

    [Fact]
    public void DecisionUnrelatedToActiveEvidenceIsExcluded()
    {
        var decision = Decision(
            new FetchId(Guid.NewGuid()),
            new CollectionJobId(Guid.NewGuid()),
            DateTimeOffset.Parse("2026-09-23T12:00:00+00:00"));

        var included = MeasurementRunner.ScheduleDecisionBelongsToActiveEvidence(
            decision,
            new HashSet<Guid>(),
            new HashSet<Guid>());

        Assert.False(included);
    }

    private static SourceMeasurementScheduleDecision Decision(
        FetchId fetchId,
        CollectionJobId? successorJobId,
        DateTimeOffset createdAt) =>
        new(
            fetchId,
            new EndpointId(Guid.NewGuid()),
            "war-api",
            "live-1",
            "live",
            "dynamic-map-state",
            "map-dynamic/TestHex",
            "warapi-poll@test",
            60_000,
            EndpointActive: true,
            ProbeSelected: false,
            SourceCacheEligibleAt: null,
            NextTargetAt: null,
            RetryEligibleAt: null,
            successorJobId,
            SuccessorAvailableAt: null,
            createdAt);
}
