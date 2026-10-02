using System.Text;
using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
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

namespace FoxData.RecoveryTests;

public sealed class M5CanonicalRecoveryTests(
    RecoveryPostgresFixture postgres)
    : IClassFixture<RecoveryPostgresFixture>
{
    [Fact]
    public async Task RawDurableWarCaptureRecoversThroughM5WithoutAnotherFetch()
    {
        await MigrateAsync();
        await ResetAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live-1",
            "live",
            TestContext.Current.CancellationToken);
        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.RuntimeWarState.Key,
            "war",
            TestContext.Current.CancellationToken);

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));

        var retrievedAt = new DateTimeOffset(
            2026, 9, 28, 4, 0, 0, TimeSpan.Zero);
        var body = Encoding.UTF8.GetBytes(
            """{"warId":"recovery-war","warNumber":132,"winner":"NONE"}""");

        await CaptureRawAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            retrievedAt,
            body);

        var fetchCountBefore =
            await CountAsync(dataSource, "evidence.fetches");

        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "runtime.wars"));

        var recovery = CreateRecoveryCoordinator(dataSource);

        var first = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.ParseRunsRepaired);
        Assert.Equal(1, first.CoverageRecorded);
        Assert.Equal(1, first.CanonicalCompleted);
        Assert.Equal(
            fetchCountBefore,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            1L,
            await CountAsync(dataSource, "runtime.wars"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "runtime.war_observations"));

        var second = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, second.ProgressCount);
        Assert.Equal(
            fetchCountBefore,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "runtime.war_observations"));
    }

    [Fact]
    public async Task CapabilityPlanControlsSelectionAndDependencyOrdering()
    {
        await MigrateAsync();
        await ResetAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live-1",
            "live",
            TestContext.Current.CancellationToken);
        var warEndpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.RuntimeWarState.Key,
            "war",
            TestContext.Current.CancellationToken);
        var mapsEndpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.ActiveMapList.Key,
            "maps",
            TestContext.Current.CancellationToken);

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));
        var retrievedAt = new DateTimeOffset(
            2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            retrievedAt,
            Encoding.UTF8.GetBytes(
                """{"warId":"plan-war","warNumber":133,"winner":"NONE"}"""));
        await CaptureRawAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            retrievedAt.AddSeconds(1),
            Encoding.UTF8.GetBytes(
                """["DeadLandsHex"]"""));

        var store = new PostgresCoverageStore(dataSource);
        var warOnly = new CoverageCapabilityPlan[]
        {
            new(
                WarApiCapabilities.RuntimeWarState.Key,
                WarApiVersions.Parser,
                WarApiVersions.WarNormalizer,
                DependencyRank: 9),
        };

        var filtered = await store.GetUncoveredAttemptsAsync(
            WarApiCatalog.SourceKey,
            warOnly,
            64,
            TestContext.Current.CancellationToken);

        var single = Assert.Single(filtered);
        Assert.Equal(
            WarApiCapabilities.RuntimeWarState.Key,
            single.CapabilityKey);

        var plan = new CoverageCapabilityPlan[]
        {
            new(
                WarApiCapabilities.ActiveMapList.Key,
                WarApiVersions.Parser,
                WarApiVersions.RegionNormalizer,
                DependencyRank: 0),
            new(
                WarApiCapabilities.RuntimeWarState.Key,
                WarApiVersions.Parser,
                WarApiVersions.WarNormalizer,
                DependencyRank: 10),
        };

        var uncovered = await store.GetUncoveredAttemptsAsync(
            WarApiCatalog.SourceKey,
            plan,
            64,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, uncovered.Count);

        var parser = new WarApiParser();
        var parseStore = new PostgresSourceParseRunStore(dataSource);
        foreach (var candidate in uncovered)
        {
            Assert.NotNull(candidate.RepresentationFetchId);
            var body = candidate.RepresentationBody
                ?? throw new InvalidOperationException(
                    "Uncovered body-bearing test candidate lost its representation body.");

            var capability = candidate.CapabilityKey switch
            {
                "runtime-war-state" =>
                    WarApiCapabilities.RuntimeWarState,
                "active-map-list" =>
                    WarApiCapabilities.ActiveMapList,
                _ => throw new InvalidOperationException(
                    $"Unexpected test capability '{candidate.CapabilityKey}'."),
            };
            var parsed = parser.Parse(
                capability,
                body);
            Assert.True(parsed.Parsed);

            var outcome = parsed.Outcome switch
            {
                WarApiParseOutcome.Parsed => "parsed",
                WarApiParseOutcome.ParsedWithUnknowns =>
                    "parsed_with_unknowns",
                _ => throw new InvalidOperationException(
                    "Test fixture must parse successfully."),
            };
            await parseStore.RecordAsync(
                new SourceParseRunWrite(
                    candidate.RepresentationFetchId.Value,
                    candidate.CapabilityKey,
                    WarApiVersions.Adapter,
                    WarApiVersions.Parser,
                    JsonStructuralFingerprinter.Algorithm,
                    parsed.StructuralFingerprint,
                    outcome,
                    parsed.UnknownPropertyCount,
                    parsed.UnknownCodeCount,
                    parsed.ErrorCode,
                    retrievedAt,
                    retrievedAt.AddMilliseconds(1),
                    parsed.SourceVersion,
                    parsed.SourceLastUpdated,
                    body.LongLength),
                TestContext.Current.CancellationToken);
        }

        var pending =
            await store.GetPendingCanonicalReprocessingAsync(
                WarApiCatalog.SourceKey,
                plan,
                64,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                WarApiCapabilities.ActiveMapList.Key,
                WarApiCapabilities.RuntimeWarState.Key,
            },
            pending.Select(item => item.CapabilityKey).ToArray());

        var invalidPlan = new CoverageCapabilityPlan[]
        {
            plan[0],
            plan[0] with { DependencyRank = 1 },
        };
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetUncoveredAttemptsAsync(
                WarApiCatalog.SourceKey,
                invalidPlan,
                64,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MapCoverageRepairsParsesLocallyWithoutStartingNormalization()
    {
        await MigrateAsync();
        await ResetAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live-1",
            "live",
            TestContext.Current.CancellationToken);
        var staticEndpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.StaticMapState.Key,
            "map-static/DeadLandsHex",
            TestContext.Current.CancellationToken);
        var dynamicEndpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.DynamicMapState.Key,
            "map-dynamic/DeadLandsHex",
            TestContext.Current.CancellationToken);

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));
        var retrievedAt = new DateTimeOffset(
            2026, 10, 1, 13, 0, 0, TimeSpan.Zero);
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "regionId": 1,
              "scorchedVictoryTowns": 0,
              "mapItems": [],
              "mapTextItems": [],
              "lastUpdated": 1790869200000,
              "version": 1
            }
            """);

        await CaptureRawAsync(
            ingestion,
            evidence,
            staticEndpoint.Resource.Id,
            retrievedAt,
            body);
        await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            retrievedAt.AddSeconds(1),
            body);

        var fetchCountBefore =
            await CountAsync(dataSource, "evidence.fetches");
        var recovery = CreateRecoveryCoordinator(dataSource);

        var first = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, first.ParseRunsRepaired);
        Assert.Equal(2, first.CoverageRecorded);
        Assert.Equal(0, first.CanonicalCompleted);
        Assert.Equal(0, first.CanonicalDeferred);
        Assert.Equal(
            fetchCountBefore,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.coverage_observations"));
        Assert.Equal(
            2L,
            await CountCoverageStateAsync(
                dataSource,
                "observed"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));

        var second = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, second.ProgressCount);
        Assert.Equal(
            fetchCountBefore,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
    }

    [Fact]
    public async Task Map304ReusesPriorRepresentationAndSingleLocalParseRepair()
    {
        await MigrateAsync();
        await ResetAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live-1",
            "live",
            TestContext.Current.CancellationToken);
        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            WarApiCapabilities.DynamicMapState.Key,
            "map-dynamic/DeadLandsHex",
            TestContext.Current.CancellationToken);

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));
        var retrievedAt = new DateTimeOffset(
            2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "regionId": 1,
              "scorchedVictoryTowns": 0,
              "mapItems": [],
              "mapTextItems": [],
              "lastUpdated": 1790872800000,
              "version": 2
            }
            """);

        var representationFetchId = await CaptureRawAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            retrievedAt,
            body,
            "m6-g3-map-body");
        await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            retrievedAt.AddMinutes(1),
            representationFetchId,
            "m6-g3-map-304");

        Assert.Equal(
            2L,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            1L,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));

        var recovery = CreateRecoveryCoordinator(dataSource);
        var first = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.ParseRunsRepaired);
        Assert.Equal(2, first.CoverageRecorded);
        Assert.Equal(0, first.CanonicalCompleted);
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            1L,
            await CountCoverageStateAsync(
                dataSource,
                "observed"));
        Assert.Equal(
            1L,
            await CountCoverageStateAsync(
                dataSource,
                "source_not_modified"));
        Assert.Equal(
            1L,
            await CountDistinctCoverageLineageAsync(dataSource));
        Assert.Equal(
            1L,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            2L,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));

        var second = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, second.ProgressCount);
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.coverage_observations"));
    }

    private static WarApiCoverageRecoveryCoordinator
        CreateRecoveryCoordinator(NpgsqlDataSource dataSource)
    {
        var options = CreateOptions();
        var normalization = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));
        var canonicalEvidence =
            new PostgresCanonicalEvidenceReader(dataSource);
        var sourceContext =
            new PostgresWarContextReader(dataSource);
        var warNormalization =
            new WarApiWarNormalizationCoordinator(
                canonicalEvidence,
                new WarCanonicalKernel(
                    new PostgresWarCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var regionNormalization =
            new WarApiRegionNormalizationCoordinator(
                canonicalEvidence,
                sourceContext,
                warNormalization,
                new RegionCanonicalKernel(
                    new PostgresRegionCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var coverageStore =
            new PostgresCoverageStore(dataSource);
        var mapContextResolver =
            new WarApiMapContextResolver(
                sourceContext,
                canonicalEvidence,
                new PostgresWarRegionReader(dataSource),
                coverageStore,
                warNormalization,
                regionNormalization,
                options);
        var reportNormalization =
            new WarApiWarReportNormalizationCoordinator(
                canonicalEvidence,
                mapContextResolver,
                new WarReportCanonicalKernel(
                    new PostgresWarReportCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);

        return new WarApiCoverageRecoveryCoordinator(
            coverageStore,
            new PostgresSourceParseRunStore(dataSource),
            sourceContext,
            warNormalization,
            regionNormalization,
            reportNormalization,
            options,
            TimeProvider.System);
    }

    private static async Task<FetchId> CaptureRawAsync(
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        FoxData.Core.Sources.EndpointId endpointId,
        DateTimeOffset retrievedAt,
        byte[] body,
        string jobKey = "m5-h-recovery")
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var queued = await ingestion.EnqueueAsync(
            endpointId,
            jobKey,
            scheduledAt,
            scheduledAt,
            cancellationToken:
                TestContext.Current.CancellationToken);
        Assert.Equal(JobEnqueueStatus.Created, queued.Status);

        var workerId = WorkerInstanceId.New();
        var claim = await ingestion.ClaimNextAsync(
            workerId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
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

        var capture = await evidence.CaptureSourceResponseAsync(
            attemptId,
            endpointId,
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
                "\"m5-h\"",
                "max-age=60",
                retrievedAt.AddMinutes(1),
                2),
            body,
            cancellationToken:
                TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        return Assert.IsType<FetchDescriptor>(capture.Fetch).Id;
    }

    private static async Task<FetchId> CaptureNotModifiedAsync(
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        FoxData.Core.Sources.EndpointId endpointId,
        DateTimeOffset retrievedAt,
        FetchId priorFetchId,
        string jobKey)
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var queued = await ingestion.EnqueueAsync(
            endpointId,
            jobKey,
            scheduledAt,
            scheduledAt,
            cancellationToken:
                TestContext.Current.CancellationToken);
        Assert.Equal(JobEnqueueStatus.Created, queued.Status);

        var workerId = WorkerInstanceId.New();
        var claim = await ingestion.ClaimNextAsync(
            workerId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
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

        var capture = await evidence.CaptureSourceResponseAsync(
            attemptId,
            endpointId,
            claim.Job.LeaseGeneration,
            fenced.Attempt!.FenceToken!.Value,
            new SourceResponseObservation(
                retrievedAt.AddMilliseconds(-2),
                retrievedAt.AddMilliseconds(-1),
                retrievedAt,
                "war-api",
                304,
                null,
                null,
                null,
                "\"m6-g3-304\"",
                "max-age=60",
                retrievedAt.AddMinutes(1),
                2),
            body: null,
            priorFetchId: priorFetchId,
            cancellationToken:
                TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        return Assert.IsType<FetchDescriptor>(capture.Fetch).Id;
    }

    private static async Task<long> CountDistinctCoverageLineageAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand(
                """
                SELECT COUNT(*)
                FROM (
                    SELECT DISTINCT
                        representation_fetch_id,
                        source_parse_run_id
                    FROM evidence.coverage_observations
                    WHERE state IN ('observed', 'source_not_modified')
                ) AS lineage;
                """);
        return (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
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
                evidence.coverage_reprocessing_runs,
                evidence.coverage_observations,
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

    private static async Task<long> CountCoverageStateAsync(
        NpgsqlDataSource dataSource,
        string state)
    {
        await using var command =
            dataSource.CreateCommand(
                """
                SELECT COUNT(*)
                FROM evidence.coverage_observations
                WHERE state = @state;
                """);
        command.Parameters.AddWithValue("state", state);
        return (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> CountAsync(
        NpgsqlDataSource dataSource,
        string tableName)
    {
        await using var command =
            dataSource.CreateCommand(
                $"SELECT COUNT(*) FROM {tableName};");
        return (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
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
            OutboundGlobalMinimumInterval:
                TimeSpan.FromMilliseconds(150),
            OutboundPerHostMinimumInterval:
                TimeSpan.FromMilliseconds(400),
            UserAgent: "FoxData-Test/M5-H-Recovery");
}
