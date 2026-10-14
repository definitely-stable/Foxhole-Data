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
    public async Task MapCoverageRepairsParsesAndNormalizesLocallyWithoutQuality()
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
        Assert.Equal(2, first.CanonicalCompleted);
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
            2L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "quality.map_quality_runs"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "runtime.map_observations"));

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
            2L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
        Assert.Equal(
            2L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));
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
        Assert.Equal(1, first.CanonicalCompleted);
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
            1L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "quality.map_quality_runs"));


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

    [Fact]
    public async Task MapParseRunWithoutNormalizationRecoversLocallyAndIdempotently()
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
            2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "regionId": 1,
              "scorchedVictoryTowns": 0,
              "mapItems": [
                {
                  "teamId": "WARDENS",
                  "iconType": 17,
                  "x": 0.25,
                  "y": 0.75,
                  "flags": 0
                }
              ],
              "mapTextItems": [
                {
                  "text": "Deadlands",
                  "x": 0.5,
                  "y": 0.5,
                  "mapMarkerType": "Major"
                }
              ],
              "lastUpdated": 1790956800000,
              "version": 3
            }
            """);

        var representationFetchId = await CaptureRawAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            retrievedAt,
            body,
            "m6-g4-normalization-gap");

        var parser = new WarApiParser();
        var parsed = parser.Parse(
            WarApiCapabilities.DynamicMapState,
            body);
        Assert.True(parsed.Parsed);

        var parseStore =
            new PostgresSourceParseRunStore(dataSource);
        _ = await parseStore.RecordAsync(
            new SourceParseRunWrite(
                representationFetchId,
                WarApiCapabilities.DynamicMapState.Key,
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
                retrievedAt.AddMilliseconds(1),
                retrievedAt.AddMilliseconds(2),
                parsed.SourceVersion,
                parsed.SourceLastUpdated,
                body.LongLength),
            TestContext.Current.CancellationToken);

        var fetchCountBefore =
            await CountAsync(dataSource, "evidence.fetches");

        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.source_parse_runs"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));

        var recovery = CreateRecoveryCoordinator(dataSource);
        var first = await recovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, first.ParseRunsRepaired);
        Assert.Equal(1, first.CoverageRecorded);
        Assert.Equal(1, first.CanonicalCompleted);
        Assert.Equal(0, first.CanonicalDeferred);
        Assert.Equal(
            fetchCountBefore,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.normalization_runs"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_item_occurrences"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_text_occurrences"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "quality.map_quality_runs"));
        Assert.Equal(
            0L,
            await CountAsync(
                dataSource,
                "runtime.map_observations"));

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
                "evidence.normalization_runs"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_snapshots"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_item_occurrences"));
        Assert.Equal(
            1L,
            await CountAsync(
                dataSource,
                "evidence.map_text_occurrences"));
    }

    [Fact]
    public async Task CrossWar304QualityRecoveryReusesSnapshotAndResetsBaseline()
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
            2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            retrievedAt,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g5-war","warNumber":134,"winner":"NONE"}"""),
            "m6-g5-war");
        var mapListRepresentationFetchId =
            await CaptureRawAsync(
                ingestion,
                evidence,
                mapsEndpoint.Resource.Id,
                retrievedAt.AddSeconds(1),
                Encoding.UTF8.GetBytes(
                    """["DeadLandsHex"]"""),
                "m6-g5-maps");
        _ = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            retrievedAt.AddSeconds(2),
            Encoding.UTF8.GetBytes(
                """
                {
                  "regionId": 1,
                  "scorchedVictoryTowns": 0,
                  "mapItems": [],
                  "mapTextItems": [],
                  "lastUpdated": 1790956800000,
                  "version": 3
                }
                """),
            "m6-g5-dynamic-1");
        var latestRepresentationFetchId = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            retrievedAt.AddSeconds(3),
            Encoding.UTF8.GetBytes(
                """
                {
                  "regionId": 1,
                  "scorchedVictoryTowns": 0,
                  "mapItems": [],
                  "mapTextItems": [],
                  "lastUpdated": 1790956801000,
                  "version": 4
                }
                """),
            "m6-g5-dynamic-2");

        var coverageRecovery = CreateRecoveryCoordinator(dataSource);
        var canonical = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(4, canonical.ParseRunsRepaired);
        Assert.Equal(4, canonical.CoverageRecorded);
        Assert.Equal(4, canonical.CanonicalCompleted);
        Assert.Equal(
            2L,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "quality.map_quality_runs"));

        var fetchCountBeforeQuality =
            await CountAsync(dataSource, "evidence.fetches");
        var payloadCountBeforeQuality =
            await CountAsync(dataSource, "evidence.payloads");
        var parseCountBeforeQuality =
            await CountAsync(dataSource, "evidence.source_parse_runs");
        var normalizationCountBeforeQuality =
            await CountAsync(dataSource, "evidence.normalization_runs");
        var snapshotCountBeforeQuality =
            await CountAsync(dataSource, "evidence.map_snapshots");

        var qualityRecovery =
            CreateQualityRecoveryCoordinator(dataSource);
        var first = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, first.TerminalCompleted);
        Assert.Equal(0, first.Deferred);
        Assert.Equal(0, first.VersionBlocked);
        Assert.Equal(
            2L,
            await CountAsync(dataSource, "quality.map_quality_runs"));
        Assert.Equal(
            2L,
            await CountAsync(dataSource, "runtime.map_observations"));
        Assert.Equal(
            fetchCountBeforeQuality,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            payloadCountBeforeQuality,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            parseCountBeforeQuality,
            await CountAsync(dataSource, "evidence.source_parse_runs"));
        Assert.Equal(
            normalizationCountBeforeQuality,
            await CountAsync(dataSource, "evidence.normalization_runs"));
        Assert.Equal(
            snapshotCountBeforeQuality,
            await CountAsync(dataSource, "evidence.map_snapshots"));

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM quality.map_quality_runs
                WHERE quality_policy_version = @policy_version;
                """;
            command.Parameters.AddWithValue(
                "policy_version",
                WarApiVersions.MapQualityPolicyV1);
            Assert.Equal(
                2L,
                (long)(await command.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
        }

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    run.baseline_map_observation_id,
                    observation.id
                FROM quality.map_quality_runs AS run
                LEFT JOIN runtime.map_observations AS observation
                    ON observation.quality_run_id = run.id
                JOIN evidence.fetches AS validation
                    ON validation.id = run.validation_fetch_id
                ORDER BY validation.retrieved_at, validation.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);

            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.True(reader.IsDBNull(0));
            var firstObservationId = reader.GetGuid(1);

            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(firstObservationId, reader.GetGuid(0));
            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        var second = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, second.ProgressCount);
        Assert.Equal(0, second.OutstandingCount);

        var sameWar304A = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            retrievedAt.AddMinutes(1),
            latestRepresentationFetchId,
            "m6-g6-same-war-304-a");
        var sameWar304B = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            retrievedAt.AddMinutes(2),
            latestRepresentationFetchId,
            "m6-g6-same-war-304-b");
        _ = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        var sameWar = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(2, sameWar.TerminalCompleted);
        Assert.Equal(0, sameWar.Deferred);
        Assert.Equal(0, sameWar.VersionBlocked);
        Assert.Equal(
            4L,
            await CountAsync(dataSource, "quality.map_quality_runs"));
        Assert.Equal(
            4L,
            await CountAsync(dataSource, "runtime.map_observations"));
        Assert.Equal(
            2L,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            snapshotCountBeforeQuality,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            payloadCountBeforeQuality,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            parseCountBeforeQuality,
            await CountAsync(dataSource, "evidence.source_parse_runs"));
        Assert.Equal(
            normalizationCountBeforeQuality,
            await CountAsync(dataSource, "evidence.normalization_runs"));

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    run.validation_fetch_id,
                    run.baseline_map_observation_id,
                    observation.id
                FROM quality.map_quality_runs AS run
                JOIN runtime.map_observations AS observation
                    ON observation.quality_run_id = run.id
                WHERE run.validation_fetch_id IN (@validation_a, @validation_b)
                ORDER BY observation.observed_at, run.validation_fetch_id;
                """;
            command.Parameters.AddWithValue(
                "validation_a",
                sameWar304A.Value);
            command.Parameters.AddWithValue(
                "validation_b",
                sameWar304B.Value);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            var first304Observation = reader.GetGuid(2);

            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(
                first304Observation,
                reader.GetGuid(1));
            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        var sameWarReplay = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, sameWarReplay.ProgressCount);
        Assert.Equal(0, sameWarReplay.OutstandingCount);

        var snapshotCountBeforeTransition =
            await CountAsync(dataSource, "evidence.map_snapshots");
        var itemCountBeforeTransition =
            await CountAsync(dataSource, "evidence.map_item_occurrences");
        var textCountBeforeTransition =
            await CountAsync(dataSource, "evidence.map_text_occurrences");
        var mapParseCountBeforeTransition =
            await CountMapSourceParseRunsAsync(dataSource);
        var mapNormalizationCountBeforeTransition =
            await CountMapNormalizationRunsAsync(dataSource);

        var warBAt = retrievedAt.AddMinutes(10);
        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            warBAt,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g7-war-2","warNumber":135,"winner":"NONE"}"""),
            "m6-g7-war-2");
        var payloadCountAfterWarBody =
            await CountAsync(dataSource, "evidence.payloads");

        var mapList304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            warBAt.AddSeconds(1),
            mapListRepresentationFetchId,
            "m6-g7-maps-war-2-304");
        var crossWar304At = warBAt.AddSeconds(2);
        var crossWar304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            crossWar304At,
            latestRepresentationFetchId,
            "m6-g7-cross-war-304");

        // The map-state 304 is durable, but M5 has not yet repaired the
        // new war/map-list context. G7 must remain retryable rather than
        // binding the old snapshot to an unproven WarRegion.
        var beforeContinuity = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, beforeContinuity.TerminalCompleted);
        Assert.Equal(1, beforeContinuity.Deferred);
        Assert.Equal(0, beforeContinuity.VersionBlocked);
        Assert.Equal(1, beforeContinuity.OutstandingCount);
        Assert.Equal(
            4L,
            await CountAsync(dataSource, "quality.map_quality_runs"));

        var transitionRecovery = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(3, transitionRecovery.CoverageRecorded);
        Assert.Equal(1, transitionRecovery.ParseRunsRepaired);
        Assert.Equal(1, transitionRecovery.ContinuityApplied);
        Assert.True(
            await new PostgresCoverageStore(dataSource)
                .IsMapContinuityAppliedAsync(
                    mapList304,
                    WarApiVersions.CoverageReprocessor,
                    TestContext.Current.CancellationToken));

        var crossWar = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, crossWar.TerminalCompleted);
        Assert.Equal(0, crossWar.Deferred);
        Assert.Equal(0, crossWar.VersionBlocked);
        Assert.Equal(0, crossWar.OutstandingCount);
        Assert.Equal(
            5L,
            await CountAsync(dataSource, "quality.map_quality_runs"));
        Assert.Equal(
            5L,
            await CountAsync(dataSource, "runtime.map_observations"));
        Assert.Equal(
            snapshotCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            itemCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_item_occurrences"));
        Assert.Equal(
            textCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_text_occurrences"));
        Assert.Equal(
            payloadCountAfterWarBody,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            mapParseCountBeforeTransition,
            await CountMapSourceParseRunsAsync(dataSource));
        Assert.Equal(
            mapNormalizationCountBeforeTransition,
            await CountMapNormalizationRunsAsync(dataSource));

        Guid warARegionId;
        Guid warASnapshotId;
        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT war_region_id, map_snapshot_id
                FROM quality.map_quality_runs
                WHERE validation_fetch_id = @validation_fetch_id;
                """;
            command.Parameters.AddWithValue(
                "validation_fetch_id",
                sameWar304B.Value);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            warARegionId = reader.GetGuid(0);
            warASnapshotId = reader.GetGuid(1);
            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        Guid firstWarBObservationId;
        Guid warBRegionId;
        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    run.war_region_id,
                    run.map_snapshot_id,
                    run.baseline_map_observation_id,
                    observation.id,
                    observation.observed_at,
                    snapshot.representation_fetch_id
                FROM quality.map_quality_runs AS run
                JOIN runtime.map_observations AS observation
                    ON observation.quality_run_id = run.id
                JOIN evidence.map_snapshots AS snapshot
                    ON snapshot.id = run.map_snapshot_id
                WHERE run.validation_fetch_id = @validation_fetch_id;
                """;
            command.Parameters.AddWithValue(
                "validation_fetch_id",
                crossWar304.Value);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));

            warBRegionId = reader.GetGuid(0);
            Assert.NotEqual(warARegionId, warBRegionId);
            Assert.Equal(warASnapshotId, reader.GetGuid(1));
            Assert.True(reader.IsDBNull(2));
            firstWarBObservationId = reader.GetGuid(3);
            Assert.Equal(
                crossWar304At,
                reader.GetFieldValue<DateTimeOffset>(4));
            Assert.Equal(
                latestRepresentationFetchId.Value,
                reader.GetGuid(5));
            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        var warBSecond304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            warBAt.AddSeconds(3),
            latestRepresentationFetchId,
            "m6-g7-war-2-304-second");
        _ = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        var warBSecond = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(1, warBSecond.TerminalCompleted);
        Assert.Equal(0, warBSecond.Deferred);
        Assert.Equal(0, warBSecond.VersionBlocked);

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    run.war_region_id,
                    run.baseline_map_observation_id
                FROM quality.map_quality_runs AS run
                WHERE run.validation_fetch_id = @validation_fetch_id;
                """;
            command.Parameters.AddWithValue(
                "validation_fetch_id",
                warBSecond304.Value);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(warBRegionId, reader.GetGuid(0));
            Assert.Equal(firstWarBObservationId, reader.GetGuid(1));
            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            snapshotCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            itemCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_item_occurrences"));
        Assert.Equal(
            textCountBeforeTransition,
            await CountAsync(dataSource, "evidence.map_text_occurrences"));
        Assert.Equal(
            payloadCountAfterWarBody,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            mapParseCountBeforeTransition,
            await CountMapSourceParseRunsAsync(dataSource));
        Assert.Equal(
            mapNormalizationCountBeforeTransition,
            await CountMapNormalizationRunsAsync(dataSource));

        var replay = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.ProgressCount);
        Assert.Equal(0, replay.OutstandingCount);

        Assert.Equal(
            WarApiVersions.MapQualityPolicyV1,
            WarApiMapQualityTarget.Live.PolicyVersion);

        var policyV2Target =
            WarApiMapQualityTarget.ForPolicyVersion(
                WarApiVersions.MapQualityPolicyV2);
        Assert.Equal(
            WarApiVersions.MapTaxonomy,
            policyV2Target.TaxonomyVersion);

        var policyV2 = await qualityRecovery.RunOnceAsync(
            policyV2Target,
            TestContext.Current.CancellationToken);
        Assert.Equal(6, policyV2.TerminalCompleted);
        Assert.Equal(0, policyV2.Deferred);
        Assert.Equal(0, policyV2.VersionBlocked);
        Assert.Equal(0, policyV2.OutstandingCount);

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    quality_policy_version,
                    taxonomy_version,
                    COUNT(*)
                FROM quality.map_quality_runs
                GROUP BY quality_policy_version, taxonomy_version
                ORDER BY quality_policy_version;
                """;

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);

            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(
                WarApiVersions.MapQualityPolicyV1,
                reader.GetString(0));
            Assert.Equal(
                WarApiVersions.MapTaxonomy,
                reader.GetString(1));
            Assert.Equal(6L, reader.GetInt64(2));

            Assert.True(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(
                WarApiVersions.MapQualityPolicyV2,
                reader.GetString(0));
            Assert.Equal(
                WarApiVersions.MapTaxonomy,
                reader.GetString(1));
            Assert.Equal(6L, reader.GetInt64(2));

            Assert.False(await reader.ReadAsync(
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            12L,
            await CountAsync(dataSource, "runtime.map_observations"));

        var policyV2Replay = await qualityRecovery.RunOnceAsync(
            policyV2Target,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, policyV2Replay.ProgressCount);
        Assert.Equal(0, policyV2Replay.OutstandingCount);

        var liveReplay = await qualityRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, liveReplay.ProgressCount);
        Assert.Equal(0, liveReplay.OutstandingCount);
        Assert.Equal(
            WarApiVersions.MapQualityPolicyV1,
            WarApiVersions.MapQualityPolicy);

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
        var mapSnapshots = new MapSnapshotKernel(
            new PostgresMapSnapshotStore(dataSource));
        var staticMapNormalization =
            new WarApiStaticMapNormalizationCoordinator(
                canonicalEvidence,
                mapSnapshots,
                normalization,
                options,
                TimeProvider.System);
        var dynamicMapNormalization =
            new WarApiDynamicMapNormalizationCoordinator(
                canonicalEvidence,
                mapSnapshots,
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
            staticMapNormalization,
            dynamicMapNormalization,
            options,
            TimeProvider.System);
    }

    private static WarApiMapQualityRecoveryCoordinator
        CreateQualityRecoveryCoordinator(NpgsqlDataSource dataSource)
    {
        var options = CreateOptions();
        var normalization = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));
        var canonicalEvidence =
            new PostgresCanonicalEvidenceReader(dataSource);
        var sourceContext =
            new PostgresWarContextReader(dataSource);
        var coverageStore =
            new PostgresCoverageStore(dataSource);
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
        var mapContext =
            new WarApiMapContextResolver(
                sourceContext,
                canonicalEvidence,
                new PostgresWarRegionReader(dataSource),
                coverageStore,
                warNormalization,
                regionNormalization,
                options);
        var qualityStore =
            new PostgresMapQualityStore(dataSource);
        var qualityCoordinator =
            new WarApiMapQualityCoordinator(
                mapContext,
                new MapSnapshotKernel(
                    new PostgresMapSnapshotStore(dataSource)),
                new PostgresMapQualityOrderingReader(dataSource),
                qualityStore,
                new MapQualityKernel(qualityStore),
                TimeProvider.System);

        return new WarApiMapQualityRecoveryCoordinator(
            new PostgresMapQualityGapReader(dataSource),
            new MapSnapshotKernel(
                new PostgresMapSnapshotStore(dataSource)),
            qualityCoordinator);
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
                runtime.map_observations,
                quality.map_quality_findings,
                quality.map_quality_runs,
                evidence.map_item_occurrences,
                evidence.map_text_occurrences,
                evidence.map_snapshots,
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

    private static async Task<long> CountMapSourceParseRunsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM evidence.source_parse_runs
            WHERE capability_key IN (
                'active-map-list',
                'dynamic-map-state');
            """);
        return (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> CountMapNormalizationRunsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM evidence.normalization_runs AS normalization
            JOIN evidence.source_parse_runs AS parse
                ON parse.id = normalization.source_parse_run_id
            WHERE parse.capability_key IN (
                'active-map-list',
                'dynamic-map-state');
            """);
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
