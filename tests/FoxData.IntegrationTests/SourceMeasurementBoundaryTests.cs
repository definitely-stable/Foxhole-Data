extern alias measurement;

using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using MeasurementActiveWindow = measurement::FoxData.SourceMeasurement.MeasurementActiveWindow;
using MeasurementRunner = measurement::FoxData.SourceMeasurement.MeasurementRunner;

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

    [Fact]
    public void FinalShutdownTailIsClassifiedWithoutHidingInteriorGaps()
    {
        var endpointId = new EndpointId(Guid.NewGuid());
        var finalWindow = new MeasurementActiveWindow(
            "s12",
            "live3",
            DateTimeOffset.Parse("2026-09-23T14:52:17+00:00"),
            DateTimeOffset.Parse("2026-09-23T18:52:17+00:00"));

        var terminalFetch = Fetch(
            endpointId,
            DateTimeOffset.Parse("2026-09-23T18:52:16.200000+00:00"));
        var interiorFetch = Fetch(
            new EndpointId(Guid.NewGuid()),
            DateTimeOffset.Parse("2026-09-23T18:52:10+00:00"));

        var attempts = new Dictionary<Guid, SourceMeasurementAttempt>
        {
            [terminalFetch.AttemptId.Value] = Attempt(
                terminalFetch,
                DateTimeOffset.Parse("2026-09-23T18:52:16.800000+00:00")),
            [interiorFetch.AttemptId.Value] = Attempt(
                interiorFetch,
                DateTimeOffset.Parse("2026-09-23T18:52:10.500000+00:00")),
        };

        var classified = MeasurementRunner.CountTerminalUnreconciledFetches(
            [terminalFetch, interiorFetch],
            [terminalFetch, interiorFetch],
            attempts,
            [finalWindow]);

        Assert.Equal(1, classified);
    }

    [Fact]
    public void TerminalFetchMustBeCompletedCapturedCurrent()
    {
        var endpointId = new EndpointId(Guid.NewGuid());
        var finalWindow = new MeasurementActiveWindow(
            "s12",
            "live3",
            DateTimeOffset.Parse("2026-09-23T14:52:17+00:00"),
            DateTimeOffset.Parse("2026-09-23T18:52:17+00:00"));
        var fetch = Fetch(
            endpointId,
            DateTimeOffset.Parse("2026-09-23T18:52:16.200000+00:00"));

        var uncertain = Attempt(
            fetch,
            DateTimeOffset.Parse("2026-09-23T18:52:16.800000+00:00")) with
        {
            State = "uncertain",
            OutcomeCode = "uncertain_exchange",
        };

        var classified = MeasurementRunner.CountTerminalUnreconciledFetches(
            [fetch],
            [fetch],
            new Dictionary<Guid, SourceMeasurementAttempt>
            {
                [fetch.AttemptId.Value] = uncertain,
            },
            [finalWindow]);

        Assert.Equal(0, classified);
    }

    private static SourceMeasurementFetch Fetch(
        EndpointId endpointId,
        DateTimeOffset requestStartedAt)
    {
        var attemptId = new IngestionAttemptId(Guid.NewGuid());
        return new SourceMeasurementFetch(
            new FetchId(Guid.NewGuid()),
            PriorFetchId: null,
            attemptId,
            endpointId,
            "official-war-api",
            "live-1",
            "live",
            "runtime-war-state",
            "war",
            requestStartedAt,
            requestStartedAt.AddMilliseconds(25),
            StatusCode: 200,
            DurationMs: 25,
            PayloadId: null,
            PayloadCreatedAt: null,
            PayloadSha256Hex: null,
            PayloadBytes: null,
            SourceEtag: null,
            CacheControl: null,
            ExpiresAt: null,
            SourceDate: null,
            SourceAgeSeconds: null,
            RetryAfter: null,
            ContentEncoding: null,
            DeclaredLength: null,
            BodyErrorCode: null);
    }

    private static SourceMeasurementAttempt Attempt(
        SourceMeasurementFetch fetch,
        DateTimeOffset completedAt) =>
        new(
            fetch.AttemptId,
            new CollectionJobId(Guid.NewGuid()),
            fetch.EndpointId,
            AttemptNumber: 1,
            JobIdempotencyKey: "test",
            fetch.SourceKey,
            fetch.ShardKey,
            fetch.Environment,
            fetch.CapabilityKey,
            fetch.SemanticKey,
            fetch.RequestStartedAt,
            fetch.RequestStartedAt,
            ExchangeAuthorizedAt: fetch.RequestStartedAt,
            RawDurableAt: fetch.RetrievedAt,
            completedAt,
            State: "completed",
            OutcomeCode: "captured_current",
            ErrorClass: null,
            ErrorCode: null);

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
