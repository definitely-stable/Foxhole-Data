using System.Net;
using System.Text;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M3OrchestrationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Full200Then304FlowKeepsOneBodyRepresentationAndUsesEtag()
    {
        await using var fixture = await CreateFixtureAsync("war-200-304");

        var body = Encoding.UTF8.GetBytes(
            """
            {
              "warId":"war-129",
              "warNumber":129,
              "winner":"NONE",
              "conquestStartTime":null,
              "conquestEndTime":null,
              "resistanceStartTime":null,
              "scheduledConquestEndTime":null,
              "requiredVictoryTowns":20,
              "shortRequiredVictoryTowns":10
            }
            """);

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                body,
                "\"war-v1\"",
                "max-age=60"));
        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now.AddMinutes(1),
                HttpStatusCode.NotModified,
                body: null,
                "\"war-v1\"",
                "max-age=60"));

        var firstJob = await fixture.EnqueueAndClaimAsync(
            fixture.WarEndpoint.Id,
            "test:first");

        await fixture.Executor.ExecuteAsync(
            firstJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        var firstSnapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstSnapshot);
        Assert.NotNull(firstSnapshot.RepresentationPayload);
        var firstFetchId = firstSnapshot.CurrentFetch.Id;
        var firstPayloadId = firstSnapshot.CurrentFetch.PayloadId;
        Assert.NotNull(firstPayloadId);

        await fixture.Reconciler.ReconcileAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        var firstPoll = await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstPoll);
        Assert.Equal(firstFetchId, firstPoll.RepresentationFetchId);
        Assert.Equal("\"war-v1\"", firstPoll.ValidatorEtag);
        Assert.Equal(firstFetchId, firstPoll.LastProcessedFetchId);

        var parseRun = await fixture.ParseRuns.GetAsync(
            firstFetchId,
            "runtime-war-state",
            WarApiVersions.Parser,
            TestContext.Current.CancellationToken);

        Assert.NotNull(parseRun);
        Assert.Equal("parsed", parseRun.Outcome);
        Assert.Equal(JsonStructuralFingerprinter.Algorithm, parseRun.FingerprintAlgorithm);

        await fixture.MakeSuccessorAvailableAsync(firstFetchId);

        var secondClaim = await fixture.Ingestion.ClaimNextForSourceAsync(
            fixture.WorkerId,
            WarApiCatalog.SourceKey,
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);

        Assert.True(secondClaim.Claimed);

        await fixture.Executor.ExecuteAsync(
            secondClaim.Job!,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Transport.SendCount);
        Assert.Null(fixture.Transport.Requests[0].IfNoneMatch);
        Assert.Equal("\"war-v1\"", fixture.Transport.Requests[1].IfNoneMatch);

        var secondSnapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(secondSnapshot);
        Assert.Equal((int)HttpStatusCode.NotModified, secondSnapshot.CurrentFetch.StatusCode);
        Assert.Null(secondSnapshot.CurrentFetch.PayloadId);
        Assert.Equal(firstFetchId, secondSnapshot.CurrentFetch.PriorFetchId);
        Assert.Equal(firstFetchId, secondSnapshot.RepresentationFetch!.Id);
        Assert.Equal(firstPayloadId, secondSnapshot.RepresentationPayload!.Id);

        await fixture.Reconciler.ReconcileAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        var secondPoll = await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(secondSnapshot.CurrentFetch.Id, secondPoll!.LastProcessedFetchId);
        Assert.Equal(firstFetchId, secondPoll.RepresentationFetchId);
        Assert.Equal("\"war-v1\"", secondPoll.ValidatorEtag);

        Assert.Equal(
            1L,
            await fixture.CountPayloadsAsync());
    }

    [Fact]
    public async Task Orphan304ClearsValidatorAndNextRequestIsUnconditional()
    {
        await using var fixture = await CreateFixtureAsync("orphan-304");

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.NotModified,
                body: null,
                "\"orphan\"",
                "max-age=60"));

        var firstJob = await fixture.EnqueueAndClaimAsync(
            fixture.WarEndpoint.Id,
            "test:orphan-304");

        await fixture.Executor.ExecuteAsync(
            firstJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        var firstSnapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);
        var poll = await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstSnapshot);
        Assert.Null(firstSnapshot.RepresentationFetch);
        Assert.NotNull(poll);
        Assert.Null(poll.RepresentationFetchId);
        Assert.Null(poll.ValidatorEtag);
        Assert.Equal(1, poll.ConsecutiveFailures);

        await fixture.MakeSuccessorAvailableAsync(
            firstSnapshot.CurrentFetch.Id);

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now.AddMinutes(1),
                HttpStatusCode.OK,
                Encoding.UTF8.GetBytes(
                    """{"warId":"war-129","warNumber":129,"winner":"NONE"}"""),
                "\"fresh\"",
                "max-age=60"));

        var secondClaim = await fixture.Ingestion.ClaimNextForSourceAsync(
            fixture.WorkerId,
            WarApiCatalog.SourceKey,
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);

        Assert.True(secondClaim.Claimed);

        await fixture.Executor.ExecuteAsync(
            secondClaim.Job!,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Transport.SendCount);
        Assert.Null(fixture.Transport.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task ExistingSuccessorBeforePollStateIsReconciledAfterCrash()
    {
        await using var fixture = await CreateFixtureAsync("successor-replay");

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                Encoding.UTF8.GetBytes(
                    """{"warId":"war-129","warNumber":129,"winner":"NONE"}"""),
                "\"war-v1\"",
                "max-age=60"));

        var job = await fixture.EnqueueAndClaimAsync(
            fixture.WarEndpoint.Id,
            "test:successor-replay");

        await fixture.Executor.ExecuteAsync(
            job,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        var snapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Null(await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken));

        var successorAt = snapshot.CurrentFetch.RetrievedAt.AddMinutes(1);
        var precreated = await fixture.Ingestion.EnqueueAsync(
            fixture.WarEndpoint.Id,
            $"after-fetch:{snapshot.CurrentFetch.Id}",
            snapshot.CurrentFetch.RetrievedAt,
            successorAt,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(JobEnqueueStatus.Created, precreated.Status);

        await fixture.Reconciler.ReconcileAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        var poll = await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(snapshot.CurrentFetch.Id, poll!.LastProcessedFetchId);
        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                $"after-fetch:{snapshot.CurrentFetch.Id}"));
    }

    [Fact]
    public async Task TransportFailureAfterAuthorizationBecomesUncertainAfterOneSend()
    {
        await using var fixture = await CreateFixtureAsync("transport-uncertain");

        fixture.Transport.EnqueueFailure(
            new HttpRequestException("synthetic connection failure"));

        var job = await fixture.EnqueueAndClaimAsync(
            fixture.WarEndpoint.Id,
            "test:transport-uncertain");

        await fixture.Executor.ExecuteAsync(
            job,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Transport.SendCount);
        Assert.Equal(0L, await fixture.CountFetchesForJobAsync(job.Id));

        var attempt = await fixture.ReadLatestAttemptAsync(job.Id);
        var storedJob = await fixture.Ingestion.GetJobAsync(
            job.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionAttemptState.Uncertain, attempt.State);
        Assert.Equal("uncertain_exchange", attempt.OutcomeCode);
        Assert.Equal(CollectionJobState.Pending, storedJob!.State);
        Assert.Null(storedJob.LeaseOwnerId);
    }

    [Fact]
    public async Task BodyErrorIsDurableButNeverBecomesReusableRepresentation()
    {
        await using var fixture = await CreateFixtureAsync("body-error");

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                body: null,
                "\"oversized\"",
                "max-age=60",
                bodyErrorCode: "body_limit_exceeded"));

        var job = await fixture.EnqueueAndClaimAsync(
            fixture.WarEndpoint.Id,
            "test:body-error");

        await fixture.Executor.ExecuteAsync(
            job,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        var snapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal("body_limit_exceeded", snapshot.CurrentFetch.BodyErrorCode);
        Assert.Null(snapshot.CurrentFetch.PayloadId);
        Assert.Null(snapshot.RepresentationFetch);
        Assert.Null(snapshot.RepresentationPayload);

        await fixture.Reconciler.ReconcileAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        var poll = await fixture.PollState.GetAsync(
            fixture.WarEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(poll);
        Assert.Equal(snapshot.CurrentFetch.Id, poll.LastProcessedFetchId);
        Assert.Null(poll.RepresentationFetchId);
        Assert.Null(poll.ValidatorEtag);
        Assert.Equal(1, poll.ConsecutiveFailures);
        Assert.NotNull(poll.RetryEligibleAt);
        Assert.Equal(0L, await fixture.CountPayloadsAsync());
    }

    [Fact]
    public async Task MapDiscoveryCreatesExactEndpointsAndKeepsHomeRegionAsymmetry()
    {
        await using var fixture = await CreateFixtureAsync("maps-discovery");

        var body = Encoding.UTF8.GetBytes(
            """["DeadLandsHex","MarbanHollow","HomeRegionC"]""");

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                body,
                "\"maps-v1\"",
                "max-age=300"));

        var job = await fixture.EnqueueAndClaimAsync(
            fixture.MapsEndpoint.Id,
            "test:maps");

        await fixture.Executor.ExecuteAsync(
            job,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            fixture.MapsEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "war-report/DeadLandsHex",
            TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "map-static/DeadLandsHex",
            TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "map-dynamic/DeadLandsHex",
            TestContext.Current.CancellationToken));

        Assert.NotNull(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "war-report/MarbanHollow",
            TestContext.Current.CancellationToken));

        Assert.NotNull(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "war-report/HomeRegionC",
            TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "map-static/HomeRegionC",
            TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Registry.GetEndpointBySemanticKeyAsync(
            fixture.Shard.Id,
            "map-dynamic/HomeRegionC",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RepeatedMapsRefreshDoesNotDuplicateInitialDiscoveryJobs()
    {
        await using var fixture = await CreateFixtureAsync("maps-refresh-idempotency");

        var body = Encoding.UTF8.GetBytes(
            """["DeadLandsHex"]""");

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                body,
                "\"maps-v1\"",
                "max-age=300"));

        var firstJob = await fixture.EnqueueAndClaimAsync(
            fixture.MapsEndpoint.Id,
            "test:maps-refresh:first");

        await fixture.Executor.ExecuteAsync(
            firstJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            fixture.MapsEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:war-report/DeadLandsHex"));
        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:map-static/DeadLandsHex"));
        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:map-dynamic/DeadLandsHex"));

        var firstSnapshot = await fixture.EvidenceReader.GetCurrentAsync(
            fixture.MapsEndpoint.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstSnapshot);

        await fixture.MakeSuccessorAvailableAsync(
            firstSnapshot.CurrentFetch.Id);

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now.AddMinutes(5),
                HttpStatusCode.OK,
                body,
                "\"maps-v2\"",
                "max-age=300"));

        var secondClaim = await fixture.Ingestion.ClaimNextForSourceAsync(
            fixture.WorkerId,
            WarApiCatalog.SourceKey,
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);

        Assert.True(secondClaim.Claimed);
        Assert.Equal(fixture.MapsEndpoint.Id, secondClaim.Job!.EndpointId);

        await fixture.Executor.ExecuteAsync(
            secondClaim.Job,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            fixture.MapsEndpoint.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:war-report/DeadLandsHex"));
        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:map-static/DeadLandsHex"));
        Assert.Equal(
            1L,
            await fixture.CountJobsByKeyAsync(
                "discover@1:map-dynamic/DeadLandsHex"));
    }

    [Fact]
    public async Task M4ProbeAcceleratesSelectedMapButCacheStillControlsSuccessor()
    {
        var probe = new WarApiMeasurementProbeProfile(
            Enabled: true,
            RunId: "m4-probe-integration",
            MaxMapsPerShard: 1,
            TargetCadence: TimeSpan.FromSeconds(15));
        await using var fixture = await CreateFixtureAsync(
            "m4-probe",
            probe);

        var activeMaps = new[]
        {
            "DeadLandsHex",
            "MarbanHollow",
        };
        var selectedMap = Assert.Single(
            WarApiMeasurementProbePolicy.SelectMaps(
                probe,
                fixture.Shard.Key,
                activeMaps));
        var otherMap = activeMaps.Single(
            mapName => !string.Equals(
                mapName,
                selectedMap,
                StringComparison.Ordinal));

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                Encoding.UTF8.GetBytes(
                    """["DeadLandsHex","MarbanHollow"]"""),
                "etag-maps-probe",
                "max-age=300"));

        var mapsJob = await fixture.EnqueueAndClaimAsync(
            fixture.MapsEndpoint.Id,
            "test:m4-probe-maps");
        await fixture.Executor.ExecuteAsync(
            mapsJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            fixture.MapsEndpoint.Id,
            TestContext.Current.CancellationToken);

        var selectedEndpoint =
            await fixture.Registry.GetEndpointBySemanticKeyAsync(
                fixture.Shard.Id,
                $"map-dynamic/{selectedMap}",
                TestContext.Current.CancellationToken);
        var otherEndpoint =
            await fixture.Registry.GetEndpointBySemanticKeyAsync(
                fixture.Shard.Id,
                $"map-dynamic/{otherMap}",
                TestContext.Current.CancellationToken);

        Assert.NotNull(selectedEndpoint);
        Assert.NotNull(otherEndpoint);

        var selectedBody = Encoding.UTF8.GetBytes(
            """
            {
              "regionId":1,
              "mapItems":[],
              "mapTextItems":[],
              "lastUpdated":1000,
              "version":10
            }
            """);
        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                selectedBody,
                "etag-selected-v10",
                "max-age=60"));

        var selectedJob = await fixture.EnqueueAndClaimAsync(
            selectedEndpoint.Id,
            "test:m4-probe-selected");
        await fixture.Executor.ExecuteAsync(
            selectedJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);

        var selectedSnapshot =
            await fixture.EvidenceReader.GetCurrentAsync(
                selectedEndpoint.Id,
                TestContext.Current.CancellationToken);
        Assert.NotNull(selectedSnapshot);

        await fixture.Reconciler.ReconcileAsync(
            selectedEndpoint.Id,
            TestContext.Current.CancellationToken);

        var selectedPoll = await fixture.PollState.GetAsync(
            selectedEndpoint.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(selectedPoll);
        Assert.Equal(
            selectedSnapshot.CurrentFetch.RetrievedAt.AddSeconds(15),
            selectedPoll.NextTargetAt);
        Assert.Equal(
            selectedSnapshot.CurrentFetch.RetrievedAt.AddSeconds(60),
            selectedPoll.SourceCacheEligibleAt);
        Assert.Contains(
            "m4-probe@1-",
            selectedPoll.PolicyVersion,
            StringComparison.Ordinal);

        var successorAvailableAt =
            await fixture.ReadJobAvailableAtByKeyAsync(
                $"after-fetch:{selectedSnapshot.CurrentFetch.Id}");
        Assert.Equal(
            selectedSnapshot.CurrentFetch.RetrievedAt.AddSeconds(60),
            successorAvailableAt);

        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now,
                HttpStatusCode.OK,
                selectedBody,
                "etag-other-v10",
                "max-age=0"));

        var otherJob = await fixture.EnqueueAndClaimAsync(
            otherEndpoint.Id,
            "test:m4-probe-other");
        await fixture.Executor.ExecuteAsync(
            otherJob,
            fixture.WorkerId,
            TestContext.Current.CancellationToken);
        await fixture.Reconciler.ReconcileAsync(
            otherEndpoint.Id,
            TestContext.Current.CancellationToken);

        var otherPoll = await fixture.PollState.GetAsync(
            otherEndpoint.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(otherPoll);
        Assert.Equal(
            fixture.Now.AddMinutes(1),
            otherPoll.NextTargetAt);
        Assert.Equal(
            "warapi-poll@1/warapi-bootstrap-profile@1",
            otherPoll.PolicyVersion);
    }

    private async Task<Fixture> CreateFixtureAsync(
        string scenario,
        WarApiMeasurementProbeProfile? measurementProbe = null)
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));
        var ingestion = new IngestionKernel(new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(new PostgresEvidenceKernelStore(dataSource));
        var evidenceReader = new PostgresEndpointEvidenceReader(dataSource);
        var pollState = new PostgresEndpointPollStateStore(dataSource);
        var parseRuns = new PostgresSourceParseRunStore(dataSource);
        var scheduleDecisions =
            new PostgresSourceScheduleDecisionStore(dataSource);

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);
        var war = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.RuntimeWarState.Key,
            "war",
            TestContext.Current.CancellationToken);
        var maps = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.ActiveMapList.Key,
            "maps",
            TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(
            2026,
            9,
            20,
            12,
            0,
            0,
            TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var options = CreateOptions();
        var transport = new QueueTransport();

        var resolver = new WarApiRegistryResolver(registry);
        var executor = new WarApiAttemptExecutor(
            ingestion,
            evidence,
            pollState,
            resolver,
            transport,
            timeProvider,
            NullLogger<WarApiAttemptExecutor>.Instance);

        var reconciler = new WarApiReconciler(
            resolver,
            evidenceReader,
            pollState,
            parseRuns,
            scheduleDecisions,
            registry,
            ingestion,
            options,
            WarApiCollectionProfile.Bootstrap,
            measurementProbe ?? WarApiMeasurementProbeProfile.Disabled,
            timeProvider,
            NullLogger<WarApiReconciler>.Instance);

        return new Fixture(
            dataSource,
            registry,
            ingestion,
            evidenceReader,
            pollState,
            parseRuns,
            scheduleDecisions,
            executor,
            reconciler,
            transport,
            shard.Resource,
            war.Resource,
            maps.Resource,
            WorkerInstanceId.New(),
            now);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            TRUNCATE TABLE
                evidence.source_parse_runs,
                ingest.endpoint_poll_state,
                evidence.fetches,
                evidence.payloads,
                ingest.endpoint_state,
                ingest.attempts,
                ingest.collection_jobs,
                sources.endpoints,
                sources.shards,
                sources.sources
            RESTART IDENTITY CASCADE;
            """);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static WarApiWorkerOptions CreateOptions() =>
        new(
            Enabled: true,
            Shards: new[] { WarApiShard.Live1 },
            LeaseDuration: TimeSpan.FromMinutes(2),
            IdleDelay: TimeSpan.FromMilliseconds(10),
            RecoveryInterval: TimeSpan.FromMilliseconds(10),
            PlannerInterval: TimeSpan.FromMilliseconds(10),
            PlannerBatchSize: 64,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            ExchangeTimeout: TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer: 4,
            MaxResponseHeadersLengthKiB: 16,
            MaxWireBytes: 1024 * 1024,
            MaxDecodedBytes: 2 * 1024 * 1024,
            MaxExpansionRatio: 20);

    private static WarApiHttpExchangeResult CreateResponse(
        DateTimeOffset timestamp,
        HttpStatusCode status,
        byte[]? body,
        string? etag,
        string? cacheControl,
        string? bodyErrorCode = null) =>
        new(
            status,
            body,
            timestamp,
            timestamp,
            timestamp,
            1,
            "application/json",
            null,
            body?.Length,
            etag,
            cacheControl,
            null,
            timestamp,
            TimeSpan.Zero,
            null,
            bodyErrorCode);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string? IfNoneMatch);

    private sealed class QueueTransport : IWarApiTransport
    {
        private readonly Queue<Func<WarApiHttpExchangeResult>> _outcomes = new();

        public List<RecordedRequest> Requests { get; } = [];

        public int SendCount => Requests.Count;

        public void Enqueue(WarApiHttpExchangeResult response) =>
            _outcomes.Enqueue(() => response);

        public void EnqueueFailure(Exception exception) =>
            _outcomes.Enqueue(() => throw exception);

        public Task<WarApiHttpExchangeResult> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(
                new RecordedRequest(
                    request.RequestUri!,
                    request.Headers.IfNoneMatch.SingleOrDefault()?.ToString()));

            if (!_outcomes.TryDequeue(out var outcome))
            {
                throw new InvalidOperationException("No fake War API outcome is queued.");
            }

            return Task.FromResult(outcome());
        }
    }

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        IngestionKernel ingestion,
        IEndpointEvidenceReader evidenceReader,
        IEndpointPollStateStore pollState,
        ISourceParseRunStore parseRuns,
        ISourceScheduleDecisionStore scheduleDecisions,
        WarApiAttemptExecutor executor,
        WarApiReconciler reconciler,
        QueueTransport transport,
        ShardDescriptor shard,
        EndpointDescriptor warEndpoint,
        EndpointDescriptor mapsEndpoint,
        WorkerInstanceId workerId,
        DateTimeOffset now) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public SourceRegistry Registry { get; } = registry;
        public IngestionKernel Ingestion { get; } = ingestion;
        public IEndpointEvidenceReader EvidenceReader { get; } = evidenceReader;
        public IEndpointPollStateStore PollState { get; } = pollState;
        public ISourceParseRunStore ParseRuns { get; } = parseRuns;
        public ISourceScheduleDecisionStore ScheduleDecisions { get; } =
            scheduleDecisions;
        public WarApiAttemptExecutor Executor { get; } = executor;
        public WarApiReconciler Reconciler { get; } = reconciler;
        public QueueTransport Transport { get; } = transport;
        public ShardDescriptor Shard { get; } = shard;
        public EndpointDescriptor WarEndpoint { get; } = warEndpoint;
        public EndpointDescriptor MapsEndpoint { get; } = mapsEndpoint;
        public WorkerInstanceId WorkerId { get; } = workerId;
        public DateTimeOffset Now { get; } = now;

        public async Task<CollectionJobDescriptor> EnqueueAndClaimAsync(
            FoxData.Core.Sources.EndpointId endpointId,
            string key)
        {
            var scheduled = Now.AddMinutes(-1);

            var queued = await Ingestion.EnqueueAsync(
                endpointId,
                key,
                scheduled,
                scheduled,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JobEnqueueStatus.Created, queued.Status);

            var claim = await Ingestion.ClaimNextForSourceAsync(
                WorkerId,
                WarApiCatalog.SourceKey,
                TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);

            Assert.True(claim.Claimed);
            Assert.Equal(endpointId, claim.Job!.EndpointId);
            return claim.Job;
        }

        public async Task MakeSuccessorAvailableAsync(
            FoxData.Core.Evidence.FetchId fetchId)
        {
            await using var command = DataSource.CreateCommand(
                """
                UPDATE ingest.collection_jobs
                SET available_at = clock_timestamp() - interval '1 second',
                    scheduled_for = LEAST(
                        scheduled_for,
                        clock_timestamp() - interval '1 second')
                WHERE idempotency_key = @key;
                """);
            command.Parameters.AddWithValue("key", $"after-fetch:{fetchId}");

            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        public async Task<DateTimeOffset> ReadJobAvailableAtByKeyAsync(
            string key)
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT available_at
                FROM ingest.collection_jobs
                WHERE idempotency_key = @key;
                """);
            command.Parameters.AddWithValue("key", key);

            var value = await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);

            return value is DateTimeOffset timestamp
                ? timestamp
                : throw new InvalidOperationException(
                    $"Collection job '{key}' was not found.");
        }

        public async Task<long> CountJobsByKeyAsync(string key)
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT COUNT(*)
                FROM ingest.collection_jobs
                WHERE idempotency_key = @key;
                """);
            command.Parameters.AddWithValue("key", key);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountFetchesForJobAsync(CollectionJobId jobId)
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT COUNT(*)
                FROM evidence.fetches AS captured_fetch
                INNER JOIN ingest.attempts AS attempt
                    ON attempt.id = captured_fetch.attempt_id
                WHERE attempt.job_id = @job_id;
                """);
            command.Parameters.AddWithValue("job_id", jobId.Value);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<IngestionAttemptDescriptor> ReadLatestAttemptAsync(
            CollectionJobId jobId)
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT id
                FROM ingest.attempts
                WHERE job_id = @job_id
                ORDER BY attempt_number DESC
                LIMIT 1;
                """);
            command.Parameters.AddWithValue("job_id", jobId.Value);

            var value = await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            var attemptId = value is Guid guid
                ? new IngestionAttemptId(guid)
                : throw new InvalidOperationException("Job has no attempt.");

            return await Ingestion.GetAttemptAsync(
                attemptId,
                TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Attempt disappeared.");
        }

        public async Task<long> CountPayloadsAsync()
        {
            await using var command = DataSource.CreateCommand(
                "SELECT COUNT(*) FROM evidence.payloads;");
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
