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
                ""war-v1"",
                "max-age=60"));
        fixture.Transport.Enqueue(
            CreateResponse(
                fixture.Now.AddMinutes(1),
                HttpStatusCode.NotModified,
                body: null,
                ""war-v1"",
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
        Assert.Equal(""war-v1"", firstPoll.ValidatorEtag);
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
        Assert.Equal(""war-v1"", fixture.Transport.Requests[1].IfNoneMatch);

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
        Assert.Equal(""war-v1"", secondPoll.ValidatorEtag);

        Assert.Equal(
            1L,
            await fixture.CountPayloadsAsync());
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
                ""maps-v1"",
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

    private async Task<Fixture> CreateFixtureAsync(string scenario)
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
            options,
            timeProvider,
            NullLogger<WarApiAttemptExecutor>.Instance);

        var reconciler = new WarApiReconciler(
            resolver,
            evidenceReader,
            pollState,
            parseRuns,
            registry,
            ingestion,
            options,
            timeProvider,
            NullLogger<WarApiReconciler>.Instance);

        return new Fixture(
            dataSource,
            registry,
            ingestion,
            evidenceReader,
            pollState,
            parseRuns,
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
        string? cacheControl) =>
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
            null);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string? IfNoneMatch);

    private sealed class QueueTransport : IWarApiTransport
    {
        private readonly Queue<WarApiHttpExchangeResult> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public int SendCount => Requests.Count;

        public void Enqueue(WarApiHttpExchangeResult response) =>
            _responses.Enqueue(response);

        public Task<WarApiHttpExchangeResult> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(
                new RecordedRequest(
                    request.RequestUri!,
                    request.Headers.IfNoneMatch.SingleOrDefault()?.ToString()));

            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake War API response is queued.");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        IngestionKernel ingestion,
        IEndpointEvidenceReader evidenceReader,
        IEndpointPollStateStore pollState,
        ISourceParseRunStore parseRuns,
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
