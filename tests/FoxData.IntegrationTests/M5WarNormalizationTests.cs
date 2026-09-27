using System.Text;
using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Infrastructure.Canonical;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M5WarNormalizationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DurableWarReplayIsIdempotentAndPreservesUnknownWinner()
    {
        await using var fixture = await CreateFixtureAsync();
        var observedAt = new DateTimeOffset(
            2026, 9, 27, 8, 30, 0, TimeSpan.Zero);

        var parseRun = await fixture.CreateParsedWarAsync(
            "live-1",
            "replay",
            """
            {
              "warId":"opaque-war-id",
              "warNumber":129,
              "winner":"FUTURE_TEAM",
              "conquestStartTime":1790496000000,
              "conquestEndTime":null,
              "resistanceStartTime":null,
              "scheduledConquestEndTime":null,
              "requiredVictoryTowns":20,
              "shortRequiredVictoryTowns":10
            }
            """,
            observedAt);

        var first = await fixture.Coordinator.NormalizeAsync(
            parseRun.Id,
            TestContext.Current.CancellationToken);
        var replay = await fixture.Coordinator.NormalizeAsync(
            parseRun.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(WarApiWarNormalizationStatus.Normalized, first.Status);
        Assert.Equal(WarApiWarNormalizationStatus.Normalized, replay.Status);
        Assert.NotNull(first.Canonical);
        Assert.NotNull(replay.Canonical);
        Assert.Equal(first.NormalizationRun.Id, replay.NormalizationRun.Id);
        Assert.Equal(first.Canonical.War.Id, replay.Canonical.War.Id);
        Assert.Equal(first.Canonical.Observation.Id, replay.Canonical.Observation.Id);
        Assert.Equal(observedAt, first.Canonical.Observation.ObservedAt);
        Assert.Equal("FUTURE_TEAM", first.Canonical.Observation.Winner);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1790496000000),
            first.Canonical.Observation.ConquestStartTime);
        Assert.Equal(1L, await fixture.CountAsync("evidence.normalization_runs"));
        Assert.Equal(1L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(1L, await fixture.CountAsync("runtime.war_observations"));
    }

    [Fact]
    public async Task ParseCapabilityMismatchFailsClosedWithoutCanonicalRows()
    {
        await using var fixture = await CreateFixtureAsync();

        var parseRun = await fixture.CreateParsedWarAsync(
            "live-1",
            "capability-mismatch",
            """{"warId":"mismatch-war","warNumber":129,"winner":"NONE"}""",
            DateTimeOffset.UtcNow,
            parseCapabilityKey: WarApiCapabilities.ActiveMapList.Key);

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Coordinator.NormalizeAsync(
                parseRun.Id,
                TestContext.Current.CancellationToken));

        Assert.Equal(0L, await fixture.CountAsync("evidence.normalization_runs"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_observations"));
    }

    [Fact]
    public async Task InvalidWarIdentityIsRejectedWithoutCanonicalRows()
    {
        await using var fixture = await CreateFixtureAsync();

        var parseRun = await fixture.CreateParsedWarAsync(
            "live-1",
            "rejected",
            """{"warId":"  ","warNumber":129,"winner":"NONE"}""",
            DateTimeOffset.UtcNow);

        var result = await fixture.Coordinator.NormalizeAsync(
            parseRun.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(WarApiWarNormalizationStatus.Rejected, result.Status);
        Assert.Null(result.Canonical);
        Assert.Equal(NormalizationRunOutcome.Rejected, result.NormalizationRun.Outcome);
        Assert.Equal("invalid_source_war_id", result.NormalizationRun.ErrorCode);
        Assert.Equal(1L, await fixture.CountAsync("evidence.normalization_runs"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_observations"));
    }

    [Fact]
    public async Task SameSourceWarIdOnDifferentShardsCreatesDifferentWars()
    {
        await using var fixture = await CreateFixtureAsync();
        var observedAt = DateTimeOffset.UtcNow;

        var live1 = await fixture.CreateParsedWarAsync(
            "live-1",
            "same-id-live1",
            """{"warId":"shared-source-id","warNumber":129,"winner":"NONE"}""",
            observedAt);
        var live2 = await fixture.CreateParsedWarAsync(
            "live-2",
            "same-id-live2",
            """{"warId":"shared-source-id","warNumber":129,"winner":"NONE"}""",
            observedAt.AddSeconds(1));

        var first = await fixture.Coordinator.NormalizeAsync(
            live1.Id,
            TestContext.Current.CancellationToken);
        var second = await fixture.Coordinator.NormalizeAsync(
            live2.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(first.Canonical);
        Assert.NotNull(second.Canonical);
        Assert.NotEqual(first.Canonical.War.Id, second.Canonical.War.Id);
        Assert.NotEqual(first.Canonical.War.ShardId, second.Canonical.War.ShardId);
        Assert.Equal(2L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_observations"));
    }

    [Fact]
    public async Task OutOfOrderReplayRebuildsWarProjectionFromImmutableObservations()
    {
        await using var fixture = await CreateFixtureAsync();
        var earlier = new DateTimeOffset(
            2026, 9, 27, 6, 0, 0, TimeSpan.Zero);
        var later = earlier.AddHours(2);

        var laterRun = await fixture.CreateParsedWarAsync(
            "live-1",
            "later",
            """{"warId":"projection-war","warNumber":130,"winner":"WARDENS"}""",
            later);
        var earlierRun = await fixture.CreateParsedWarAsync(
            "live-1",
            "earlier",
            """{"warId":"projection-war","warNumber":129,"winner":"NONE"}""",
            earlier);

        var laterResult = await fixture.Coordinator.NormalizeAsync(
            laterRun.Id,
            TestContext.Current.CancellationToken);
        var earlierResult = await fixture.Coordinator.NormalizeAsync(
            earlierRun.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(laterResult.Canonical);
        Assert.NotNull(earlierResult.Canonical);
        Assert.Equal(laterResult.Canonical.War.Id, earlierResult.Canonical.War.Id);
        Assert.Equal(earlier, earlierResult.Canonical.War.FirstObservedAt);
        Assert.Equal(later, earlierResult.Canonical.War.LastObservedAt);
        Assert.Equal(130, earlierResult.Canonical.War.WarNumber);
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_observations"));
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));
        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);

        var normalization = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));
        var warCanonical = new WarCanonicalKernel(
            new PostgresWarCanonicalStore(dataSource));
        var options = CreateOptions();

        return new Fixture(
            dataSource,
            registry,
            source.Resource.Id,
            new IngestionKernel(
                new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(
                new PostgresEvidenceKernelStore(dataSource)),
            new PostgresSourceParseRunStore(dataSource),
            new WarApiWarNormalizationCoordinator(
                new PostgresCanonicalEvidenceReader(dataSource),
                warCanonical,
                normalization,
                options,
                TimeProvider.System));
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(
            TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync()
    {
        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            TRUNCATE TABLE
                runtime.war_report_observations,
                runtime.war_observations,
                runtime.war_regions,
                runtime.regions,
                runtime.wars,
                evidence.normalization_runs,
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

        await command.ExecuteNonQueryAsync(
            TestContext.Current.CancellationToken);
    }

    private static WarApiWorkerOptions CreateOptions() =>
        new(
            Enabled: false,
            Shards: [WarApiShard.Live1],
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
            MaxExpansionRatio: 20,
            OutboundGlobalMinimumInterval: TimeSpan.FromMilliseconds(150),
            OutboundPerHostMinimumInterval: TimeSpan.FromMilliseconds(400),
            UserAgent: "FoxData-Test/M5");

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        FoxData.Core.Sources.SourceId sourceId,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        WarApiWarNormalizationCoordinator coordinator) : IAsyncDisposable
    {
        public WarApiWarNormalizationCoordinator Coordinator { get; } =
            coordinator;

        public async Task<SourceParseRunDescriptor> CreateParsedWarAsync(
            string shardKey,
            string idempotencyKey,
            string json,
            DateTimeOffset retrievedAt,
            string? parseCapabilityKey = null)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                WarApiCapabilities.RuntimeWarState.Key,
                "war",
                TestContext.Current.CancellationToken);

            var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var queued = await ingestion.EnqueueAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                scheduledAt,
                scheduledAt,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JobEnqueueStatus.Created, queued.Status);

            var workerId = WorkerInstanceId.New();
            var claim = await ingestion.ClaimNextAsync(
                workerId,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.True(claim.Claimed);
            Assert.Equal(endpoint.Resource.Id, claim.Job!.EndpointId);

            var attemptId = IngestionAttemptId.New();
            var begun = await ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job.Id,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(BeginAttemptStatus.Started, begun.Status);

            var fenced = await ingestion.AcquireEndpointFenceAsync(
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

            var authorized = await ingestion.AuthorizeExchangeAsync(
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ExchangeAuthorizationStatus.AuthorizedNow,
                authorized.Status);

            var body = Encoding.UTF8.GetBytes(json);
            var capture = await evidence.CaptureSourceResponseAsync(
                attemptId,
                endpoint.Resource.Id,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value,
                new SourceResponseObservation(
                    retrievedAt.AddMilliseconds(-2),
                    retrievedAt.AddMilliseconds(-1),
                    retrievedAt,
                    "war-api",
                    200,
                    "application/json",
                    null,
                    body.LongLength,
                    "\"m5\"",
                    "max-age=60",
                    retrievedAt.AddMinutes(1),
                    2),
                body,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);

            var parsed = new WarApiParser().Parse(
                WarApiCapabilities.RuntimeWarState,
                body);
            Assert.True(parsed.Parsed);

            var parseStartedAt = retrievedAt.AddMilliseconds(1);
            return await parseRuns.RecordAsync(
                new SourceParseRunWrite(
                    capture.Fetch!.Id,
                    parseCapabilityKey ?? WarApiCapabilities.RuntimeWarState.Key,
                    WarApiVersions.Adapter,
                    WarApiVersions.Parser,
                    JsonStructuralFingerprinter.Algorithm,
                    parsed.StructuralFingerprint,
                    parsed.Outcome == WarApiParseOutcome.Parsed
                        ? "parsed"
                        : "parsed_with_unknowns",
                    parsed.UnknownPropertyCount,
                    parsed.UnknownCodeCount,
                    parsed.ErrorCode,
                    parseStartedAt,
                    parseStartedAt.AddMilliseconds(1),
                    parsed.SourceVersion,
                    parsed.SourceLastUpdated,
                    body.LongLength),
                TestContext.Current.CancellationToken);
        }

        public async Task<long> CountAsync(string tableName)
        {
            await using var command = dataSource.CreateCommand(
                $"SELECT COUNT(*) FROM {tableName};");
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }
}
