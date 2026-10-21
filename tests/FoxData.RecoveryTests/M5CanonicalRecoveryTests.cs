using System.Text;
using System.Text.Json;
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
        var policyV1Target =
            WarApiMapQualityTarget.ForPolicyVersion(
                WarApiVersions.MapQualityPolicyV1);
        var first = await qualityRecovery.RunOnceAsync(
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
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
            policyV1Target,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.ProgressCount);
        Assert.Equal(0, replay.OutstandingCount);

        Assert.Equal(
            WarApiVersions.MapQualityPolicyV2,
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
            WarApiVersions.MapQualityPolicyV2,
            WarApiVersions.MapQualityPolicy);

    }

    [Fact]
    public async Task PolicyV2OrderedRebuildResumesAndPreservesVersionIsolation()
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

        var at = new DateTimeOffset(
            2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            at,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g8c-war-a","warNumber":136,"winner":"NONE"}"""),
            "m6-g8c-war-a");

        var mapsRepresentation = await CaptureRawAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            at.AddSeconds(1),
            Encoding.UTF8.GetBytes(
                """["DeadLandsHex"]"""),
            "m6-g8c-maps-a");

        var neutral = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(2),
            CreateDynamicPayload(
                version: 1,
                lastUpdated: 1_700_000_000_000,
                NeutralItems()),
            "m6-g8c-neutral");

        var healthyBaseline = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(3),
            CreateDynamicPayload(
                version: 8,
                lastUpdated: 1_700_000_010_000,
                HealthyItems()),
            "m6-g8c-healthy-baseline");

        var versionRegression = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(4),
            CreateDynamicPayload(
                version: 4,
                lastUpdated: 1_700_000_020_000,
                HealthyItems()),
            "m6-g8c-version-regression");

        var timestampRegression = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(5),
            CreateDynamicPayload(
                version: 5,
                lastUpdated: 1_699_999_000_000,
                HealthyItems()),
            "m6-g8c-time-regression");

        var massDisappearance = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(6),
            CreateDynamicPayload(
                version: 6,
                lastUpdated: 1_700_000_030_000,
                HealthyItems().Take(8).ToArray()),
            "m6-g8c-mass-disappearance");

        var nearEmpty = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(7),
            CreateDynamicPayload(
                version: 7,
                lastUpdated: 1_700_000_040_000,
                HealthyItems().Take(2).ToArray()),
            "m6-g8c-near-empty");

        var restartCollapse = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(8),
            CreateDynamicPayload(
                version: 1,
                lastUpdated: 1_700_000_050_000,
                HealthyItems()
                    .Take(18)
                    .Select(item => item with { TeamId = "NONE" })
                    .ToArray()),
            "m6-g8c-restart-collapse");

        var duplicate = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(9),
            CreateDynamicPayload(
                version: 9,
                lastUpdated: 1_700_000_060_000,
                HealthyItems()
                    .Concat([HealthyItems()[0]])
                    .ToArray()),
            "m6-g8c-duplicate");

        var unknownTaxonomy = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(10),
            CreateDynamicPayload(
                version: 10,
                lastUpdated: 1_700_000_070_000,
                UnknownTaxonomyItems()),
            "m6-g8c-unknown-taxonomy");

        var healthyRecovery = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(11),
            CreateDynamicPayload(
                version: 11,
                lastUpdated: 1_700_000_080_000,
                HealthyItems()),
            "m6-g8c-healthy-recovery");

        var coverageRecovery = CreateRecoveryCoordinator(dataSource);
        var initialCanonical = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(12, initialCanonical.ParseRunsRepaired);
        Assert.Equal(12, initialCanonical.CoverageRecorded);
        Assert.Equal(12, initialCanonical.CanonicalCompleted);

        var sameWar304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddMinutes(1),
            healthyRecovery,
            "m6-g8c-same-war-304");
        _ = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        var warBAt = at.AddMinutes(10);
        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            warBAt,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g8c-war-b","warNumber":137,"winner":"NONE"}"""),
            "m6-g8c-war-b");
        var maps304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            warBAt.AddSeconds(1),
            mapsRepresentation,
            "m6-g8c-maps-b-304");
        var crossWar304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            warBAt.AddSeconds(2),
            healthyRecovery,
            "m6-g8c-cross-war-304");

        var transition = await coverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.True(transition.ContinuityApplied >= 1);
        Assert.True(
            await new PostgresCoverageStore(dataSource)
                .IsMapContinuityAppliedAsync(
                    maps304,
                    WarApiVersions.CoverageReprocessor,
                    TestContext.Current.CancellationToken));

        var policyV1Target =
            WarApiMapQualityTarget.ForPolicyVersion(
                WarApiVersions.MapQualityPolicyV1);
        var v1Recovery =
            CreateQualityRecoveryCoordinator(dataSource);
        var live = await v1Recovery.RunOnceAsync(
            policyV1Target,
            TestContext.Current.CancellationToken);
        Assert.Equal(12, live.TerminalCompleted);
        Assert.Equal(0, live.Deferred);
        Assert.Equal(0, live.VersionBlocked);
        Assert.Equal(0, live.OutstandingCount);

        var v1Signature = await ReadPolicyQualitySignatureAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV1);

        var fetchCount =
            await CountAsync(dataSource, "evidence.fetches");
        var payloadCount =
            await CountAsync(dataSource, "evidence.payloads");
        var parseCount =
            await CountAsync(dataSource, "evidence.source_parse_runs");
        var normalizationCount =
            await CountAsync(dataSource, "evidence.normalization_runs");
        var snapshotCount =
            await CountAsync(dataSource, "evidence.map_snapshots");
        var itemCount =
            await CountAsync(dataSource, "evidence.map_item_occurrences");
        var textCount =
            await CountAsync(dataSource, "evidence.map_text_occurrences");

        var target = WarApiMapQualityTarget.ForPolicyVersion(
            WarApiVersions.MapQualityPolicyV2);

        // Simulate a partial rebuild before process loss: persist only the
        // earliest @2 candidate, then discard the coordinator instance.
        var neutralSnapshot = await ReadSnapshotForRepresentationAsync(
            dataSource,
            neutral);
        var partial = await CreateQualityCoordinator(dataSource)
            .EvaluateAsync(
                neutralSnapshot,
                shard.Resource.Id,
                neutral,
                at.AddSeconds(2),
                target,
                TestContext.Current.CancellationToken);
        Assert.Equal(WarApiMapQualityStatus.Accepted, partial.Status);

        // A fresh coordinator must resume solely from durable @2 state.
        var resumed = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(
                target,
                TestContext.Current.CancellationToken);
        Assert.Equal(11, resumed.TerminalCompleted);
        Assert.Equal(0, resumed.Deferred);
        Assert.Equal(0, resumed.VersionBlocked);
        Assert.Equal(0, resumed.OutstandingCount);

        Assert.Equal(
            v1Signature,
            await ReadPolicyQualitySignatureAsync(
                dataSource,
                WarApiVersions.MapQualityPolicyV1));

        Assert.Equal(
            fetchCount,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            payloadCount,
            await CountAsync(dataSource, "evidence.payloads"));
        Assert.Equal(
            parseCount,
            await CountAsync(dataSource, "evidence.source_parse_runs"));
        Assert.Equal(
            normalizationCount,
            await CountAsync(dataSource, "evidence.normalization_runs"));
        Assert.Equal(
            snapshotCount,
            await CountAsync(dataSource, "evidence.map_snapshots"));
        Assert.Equal(
            itemCount,
            await CountAsync(dataSource, "evidence.map_item_occurrences"));
        Assert.Equal(
            textCount,
            await CountAsync(dataSource, "evidence.map_text_occurrences"));

        await AssertQualityAsync(
            dataSource,
            neutral,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertQualityAsync(
            dataSource,
            healthyBaseline,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2,
            "source-version.gap");
        await AssertQualityAsync(
            dataSource,
            versionRegression,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2,
            "source-version.regression");
        await AssertQualityAsync(
            dataSource,
            timestampRegression,
            MapQualityDecision.Suspect,
            WarApiVersions.MapQualityPolicyV2,
            "source-last-updated.regression");
        await AssertQualityAsync(
            dataSource,
            massDisappearance,
            MapQualityDecision.Suspect,
            WarApiVersions.MapQualityPolicyV2,
            "representation.mass-disappearance");
        await AssertQualityAsync(
            dataSource,
            nearEmpty,
            MapQualityDecision.Suspect,
            WarApiVersions.MapQualityPolicyV2,
            "representation.near-empty",
            "representation.mass-disappearance");
        await AssertQualityAsync(
            dataSource,
            restartCollapse,
            MapQualityDecision.Quarantined,
            WarApiVersions.MapQualityPolicyV2,
            "source-version.regression",
            "ownership.restart-collapse");
        await AssertQualityAsync(
            dataSource,
            duplicate,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2,
            "source-version.gap",
            "representation.duplicate-occurrence");
        await AssertQualityAsync(
            dataSource,
            unknownTaxonomy,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2,
            "taxonomy.unknown-icon",
            "taxonomy.unknown-team",
            "taxonomy.unknown-flag-bits");
        await AssertQualityAsync(
            dataSource,
            healthyRecovery,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertQualityAsync(
            dataSource,
            sameWar304,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertQualityAsync(
            dataSource,
            crossWar304,
            MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);

        var regressionObservation = await ReadObservationIdAsync(
            dataSource,
            versionRegression,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(regressionObservation);

        foreach (var blocked in new[]
                 {
                     timestampRegression,
                     massDisappearance,
                     nearEmpty,
                     restartCollapse,
                 })
        {
            Assert.Null(
                await ReadObservationIdAsync(
                    dataSource,
                    blocked,
                    WarApiVersions.MapQualityPolicyV2));
            Assert.Equal(
                regressionObservation,
                await ReadBaselineObservationIdAsync(
                    dataSource,
                    blocked,
                    WarApiVersions.MapQualityPolicyV2));
        }

        var duplicateObservation = await ReadObservationIdAsync(
            dataSource,
            duplicate,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(duplicateObservation);
        Assert.Equal(
            regressionObservation,
            await ReadBaselineObservationIdAsync(
                dataSource,
                duplicate,
                WarApiVersions.MapQualityPolicyV2));

        var unknownObservation = await ReadObservationIdAsync(
            dataSource,
            unknownTaxonomy,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(unknownObservation);
        Assert.Equal(
            duplicateObservation,
            await ReadBaselineObservationIdAsync(
                dataSource,
                unknownTaxonomy,
                WarApiVersions.MapQualityPolicyV2));

        var recoveryObservation = await ReadObservationIdAsync(
            dataSource,
            healthyRecovery,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(recoveryObservation);
        Assert.Equal(
            unknownObservation,
            await ReadBaselineObservationIdAsync(
                dataSource,
                healthyRecovery,
                WarApiVersions.MapQualityPolicyV2));
        Assert.Equal(
            recoveryObservation,
            await ReadBaselineObservationIdAsync(
                dataSource,
                sameWar304,
                WarApiVersions.MapQualityPolicyV2));
        Assert.Null(
            await ReadBaselineObservationIdAsync(
                dataSource,
                crossWar304,
                WarApiVersions.MapQualityPolicyV2));

        var replay = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(
                target,
                TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.ProgressCount);
        Assert.Equal(0, replay.Deferred);
        Assert.Equal(0, replay.VersionBlocked);
        Assert.Equal(0, replay.OutstandingCount);

        // M6-H/H2: the full G8-C two-war/200/304 quality graph must also be
        // idle through BOTH local recovery stages, not only quality scanning.
        var completionPasses = await M6CompletionConvergence.RunAsync(
            ct => CreateRecoveryCoordinator(dataSource).RunOnceAsync(ct),
            ct => CreateQualityRecoveryCoordinator(dataSource)
                .RunOnceAsync(target, ct),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, completionPasses);
        Assert.Equal(
            fetchCount,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            payloadCount,
            await CountAsync(dataSource, "evidence.payloads"));

        // H3/H4 independently audits the stored graph without asking the
        // recovery writers to validate data they originally persisted.
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource,
            TestContext.Current.CancellationToken);

        // A corrupted item count satisfies the database CHECK and FKs.
        // Recovery still sees no gaps, but the independent audit must fail.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE evidence.map_snapshots
                         SET item_count = item_count + 1
                         WHERE representation_fetch_id = @fetch_id;
                         """))
        {
            corrupt.Parameters.AddWithValue("fetch_id", duplicate.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var corruptOccurrences = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource,
            TestContext.Current.CancellationToken);
        Assert.Equal(1L, corruptOccurrences["snapshot_occurrences"]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => M6CompletionProvenanceAudit.AssertCleanAsync(
                dataSource,
                TestContext.Current.CancellationToken));

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE evidence.map_snapshots
                         SET item_count = item_count - 1
                         WHERE representation_fetch_id = @fetch_id;
                         """))
        {
            restore.Parameters.AddWithValue("fetch_id", duplicate.Value);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource,
            TestContext.Current.CancellationToken);

        // Null is an allowed persisted baseline, but incorrect after a
        // previous accepted quality observation under the same @2 policy.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_runs
                         SET baseline_map_observation_id = NULL
                         WHERE validation_fetch_id = @fetch_id
                           AND quality_policy_version = @policy_version;
                         """))
        {
            corrupt.Parameters.AddWithValue("fetch_id", duplicate.Value);
            corrupt.Parameters.AddWithValue(
                "policy_version", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var corruptBaseline = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource,
            TestContext.Current.CancellationToken);
        Assert.Equal(1L, corruptBaseline["baseline_binding"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_runs
                         SET baseline_map_observation_id = @baseline_id
                         WHERE validation_fetch_id = @fetch_id
                           AND quality_policy_version = @policy_version;
                         """))
        {
            restore.Parameters.AddWithValue(
                "baseline_id", regressionObservation.Value);
            restore.Parameters.AddWithValue("fetch_id", duplicate.Value);
            restore.Parameters.AddWithValue(
                "policy_version", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource,
            TestContext.Current.CancellationToken);

        // M6-H snapshot provenance cannot be reduced to the database FK:
        // the normalizer identity itself must be the frozen map version.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE evidence.normalization_runs AS run
                         SET normalizer_version =
                             'warapi-dynamic-map-normalizer@tampered'
                         FROM evidence.map_snapshots AS snapshot
                         WHERE snapshot.normalization_run_id = run.id
                           AND snapshot.representation_fetch_id = @fetch;
                         """))
        {
            corrupt.Parameters.AddWithValue("fetch", duplicate.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var wrongNormalizer = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1L, wrongNormalizer["snapshot_lineage"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE evidence.normalization_runs AS run
                         SET normalizer_version =
                             'warapi-dynamic-map-normalizer@1'
                         FROM evidence.map_snapshots AS snapshot
                         WHERE snapshot.normalization_run_id = run.id
                           AND snapshot.representation_fetch_id = @fetch;
                         """))
        {
            restore.Parameters.AddWithValue("fetch", duplicate.Value);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // H3/H4-C: faults below are allowed by relational FKs/CHECKs,
        // but violate the persisted M6 semantic contract. The auditor
        // must independently reject each one, then accept the restoration.
        // A suspect run has no accepted MapObservation, so its validation
        // can be tampered with without tripping the composite observation FK.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_runs
                         SET validation_fetch_id = @wrong_fetch
                         WHERE validation_fetch_id = @fetch
                           AND quality_policy_version = @policy;
                         """))
        {
            corrupt.Parameters.AddWithValue(
                "wrong_fetch", healthyBaseline.Value);
            corrupt.Parameters.AddWithValue(
                "fetch", timestampRegression.Value);
            corrupt.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var wrongValidation = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1L, wrongValidation["quality_lineage"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_runs
                         SET validation_fetch_id = @fetch
                         WHERE validation_fetch_id = @wrong_fetch
                           AND quality_policy_version = @policy
                           AND decision = 'suspect';
                         """))
        {
            restore.Parameters.AddWithValue(
                "fetch", timestampRegression.Value);
            restore.Parameters.AddWithValue(
                "wrong_fetch", healthyBaseline.Value);
            restore.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // Timing is not enforced by a FK: an accepted observation can
        // retain perfectly valid IDs while claiming an incorrect boundary.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE runtime.map_observations
                         SET observed_at = observed_at + interval '1 second'
                         WHERE id = @observation;
                         """))
        {
            corrupt.Parameters.AddWithValue(
                "observation", duplicateObservation.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var wrongObservationTime =
            await M6CompletionProvenanceAudit.ReadAsync(
                dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1L, wrongObservationTime["observation_binding"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE runtime.map_observations
                         SET observed_at = observed_at - interval '1 second'
                         WHERE id = @observation;
                         """))
        {
            restore.Parameters.AddWithValue(
                "observation", duplicateObservation.Value);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // An accepted run must not carry a blocking finding even if the
        // finding row itself satisfies every schema constraint.
        string originalFindingEffect;
        await using (var original = dataSource.CreateCommand(
                         """
                         SELECT finding.effect
                         FROM quality.map_quality_findings AS finding
                         JOIN quality.map_quality_runs AS run
                           ON run.id = finding.quality_run_id
                         WHERE run.validation_fetch_id = @fetch
                           AND run.quality_policy_version = @policy
                           AND finding.rule_key =
                               'representation.duplicate-occurrence';
                         """))
        {
            original.Parameters.AddWithValue("fetch", duplicate.Value);
            original.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            originalFindingEffect = Assert.IsType<string>(
                await original.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken));
        }
        Assert.NotEqual("quarantined", originalFindingEffect);

        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_findings AS finding
                         SET effect = 'quarantined'
                         FROM quality.map_quality_runs AS run
                         WHERE run.id = finding.quality_run_id
                           AND run.validation_fetch_id = @fetch
                           AND run.quality_policy_version = @policy
                           AND finding.rule_key =
                               'representation.duplicate-occurrence';
                         """))
        {
            corrupt.Parameters.AddWithValue("fetch", duplicate.Value);
            corrupt.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var wrongFinding = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1L, wrongFinding["finding_effects"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE quality.map_quality_findings AS finding
                         SET effect = @original_effect
                         FROM quality.map_quality_runs AS run
                         WHERE run.id = finding.quality_run_id
                           AND run.validation_fetch_id = @fetch
                           AND run.quality_policy_version = @policy
                           AND finding.rule_key =
                               'representation.duplicate-occurrence';
                         """))
        {
            restore.Parameters.AddWithValue(
                "original_effect", originalFindingEffect);
            restore.Parameters.AddWithValue("fetch", duplicate.Value);
            restore.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // A different positive sourceRegionId is schema-representable,
        // but it contradicts the sourceRegionId of accepted snapshots.
        await using (var corrupt = dataSource.CreateCommand(
                         """
                         UPDATE runtime.war_regions
                         SET source_region_id = source_region_id + 10000
                         WHERE id = (
                             SELECT war_region_id
                             FROM quality.map_quality_runs
                             WHERE validation_fetch_id = @fetch
                               AND quality_policy_version = @policy
                         )
                           AND source_region_id IS NOT NULL;
                         """))
        {
            corrupt.Parameters.AddWithValue("fetch", duplicate.Value);
            corrupt.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }

        var wrongRegion = await M6CompletionProvenanceAudit.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1L, wrongRegion["region_enrichment"]);

        await using (var restore = dataSource.CreateCommand(
                         """
                         UPDATE runtime.war_regions
                         SET source_region_id = source_region_id - 10000
                         WHERE id = (
                             SELECT war_region_id
                             FROM quality.map_quality_runs
                             WHERE validation_fetch_id = @fetch
                               AND quality_policy_version = @policy
                         );
                         """))
        {
            restore.Parameters.AddWithValue("fetch", duplicate.Value);
            restore.Parameters.AddWithValue(
                "policy", WarApiVersions.MapQualityPolicyV2);
            Assert.Equal(1, await restore.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));
        }
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // H3/H4-B: source-first audit agrees with snapshot-first integrity
        // after complete @2 recovery of both war contexts and 200/304.
        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);

        // A new, authoritative cross-war 304 exists before recovery scans it.
        // Snapshot-first reads remain valid; source-first completeness must
        // report both the unrecorded coverage and the unbound quality result.
        var unrecovered304 = await CaptureNotModifiedAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            warBAt.AddSeconds(3),
            healthyRecovery,
            "m6-h-source-first-304");
        var durable304FetchCount =
            await CountAsync(dataSource, "evidence.fetches");
        var durable304PayloadCount =
            await CountAsync(dataSource, "evidence.payloads");
        var pending304 = await M6CompletionChronologyAudit.ReadAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        Assert.Equal(1L, pending304["missing_coverage"]);
        Assert.Equal(1L, pending304["missing_quality"]);
        Assert.Equal(0L, pending304["invalid_validation"]);

        var coverage304 = await CreateRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, coverage304.CoverageRecorded);

        var pendingQuality = await M6CompletionChronologyAudit.ReadAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, pendingQuality["missing_coverage"]);
        Assert.Equal(1L, pendingQuality["missing_quality"]);

        var quality304 = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(1, quality304.TerminalCompleted);
        Assert.NotNull(await ReadObservationIdAsync(
            dataSource, unrecovered304, WarApiVersions.MapQualityPolicyV2));
        Assert.Equal(
            durable304FetchCount,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            durable304PayloadCount,
            await CountAsync(dataSource, "evidence.payloads"));

        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);

        // Capture a new 200 but interrupt before the current parser/normalizer.
        // A snapshot-rooted scanner cannot find this gap.
        var unrecovered200 = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            warBAt.AddSeconds(4),
            CreateDynamicPayload(
                version: 12,
                lastUpdated: 1_700_000_090_000,
                HealthyItems()),
            "m6-h-source-first-200");
        var durable200FetchCount =
            await CountAsync(dataSource, "evidence.fetches");
        var durable200PayloadCount =
            await CountAsync(dataSource, "evidence.payloads");
        var pending200 = await M6CompletionChronologyAudit.ReadAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        Assert.Equal(1L, pending200["missing_coverage"]);
        Assert.Equal(1L, pending200["missing_parse"]);

        var canonical200 = await CreateRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, canonical200.ParseRunsRepaired);
        Assert.Equal(1, canonical200.CanonicalCompleted);

        var normalizedNotQualified = await M6CompletionChronologyAudit.ReadAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, normalizedNotQualified["missing_parse"]);
        Assert.Equal(1L, normalizedNotQualified["missing_quality"]);

        var qualified200 = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(1, qualified200.TerminalCompleted);
        Assert.NotNull(await ReadObservationIdAsync(
            dataSource, unrecovered200, WarApiVersions.MapQualityPolicyV2));
        Assert.Equal(
            durable200FetchCount,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            durable200PayloadCount,
            await CountAsync(dataSource, "evidence.payloads"));

        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource,
            WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource,
            TestContext.Current.CancellationToken);

        // The G8-C fixture added two new Fetches after its historical @1
        // signature check. Bring @1 up to date *before* freezing the
        // semantic oracle, otherwise a correct rebuild would add new @1
        // quality rows and generate a false semantic inequality.
        var finalHistoricalRecovery =
            await CreateQualityRecoveryCoordinator(dataSource).RunOnceAsync(
                policyV1Target, TestContext.Current.CancellationToken);
        Assert.Equal(2, finalHistoricalRecovery.TerminalCompleted);
        Assert.Equal(0, finalHistoricalRecovery.OutstandingCount);
        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource, WarApiVersions.MapQualityPolicyV1,
            TestContext.Current.CancellationToken);
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // H1 representative static-stream and text-occurrence extension.
        // Both static representations and the 304 belong to War B; the
        // older dynamic history and its versioned baselines remain intact.
        var staticInitial = await CaptureRawAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(5), CreateStaticTextPayload(1, false),
            "m6-h1-static-initial");
        var staticChanged = await CaptureRawAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(6), CreateStaticTextPayload(2, true),
            "m6-h1-static-changed");
        var static304 = await CaptureNotModifiedAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(7), staticChanged,
            "m6-h1-static-304");
        var unavailable = await CaptureRawAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(8), null,
            "m6-h1-source-unavailable", statusCode: 503);
        var rejected = await CaptureRawAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(9), Encoding.UTF8.GetBytes("{invalid json"),
            "m6-h1-rejected");

        // H1-D: even if partial bytes were durably captured, an HTTP 200
        // body read failure is not an authoritative map representation.
        var bodyFailure = await CaptureRawAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(10), body: Encoding.UTF8.GetBytes("{partial"),
            jobKey: "m6-h1-body-failure", bodyErrorCode: "body_read_failed");

        // A 304 without a body-bearing prior is not proof of unchanged state.
        // Deliberately omit priorFetchId using the normal Evidence Kernel.
        var orphan304 = await CaptureNotModifiedAsync(
            ingestion, evidence, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(11), priorFetchId: null,
            jobKey: "m6-h1-orphan-304");

        // Exercise real expired-lease recovery, not a forged database state:
        // interruption before exchange -> collector_unavailable, while an
        // authorized but unobserved exchange -> unknown/uncertain.
        var abandonedAttempt = await RecoverInterruptedMapAttemptAsync(
            dataSource, ingestion, staticEndpoint.Resource.Id,
            "m6-h1-abandoned-exchange", authorizeExchange: false);
        var uncertainAttempt = await RecoverInterruptedMapAttemptAsync(
            dataSource, ingestion, staticEndpoint.Resource.Id,
            "m6-h1-uncertain-exchange", authorizeExchange: true);

        // H1-E: the old, authorized exchange expires. A new lease and
        // fencing token capture the authoritative response FIRST; the
        // obsolete exchange is then persisted as captured_late evidence.
        var oldExchange = await BeginMapExchangeAsync(
            ingestion, staticEndpoint.Resource.Id, "m6-h1e-late-map");
        await ExpireMapLeaseAsync(dataSource, oldExchange.JobId);
        var ingestionRecovery = new IngestionRecovery(
            new PostgresIngestionRecoveryStore(dataSource),
            new IngestionRecoveryLimits(batchSize: 8));
        var expired = await ingestionRecovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RecoveryDisposition.MarkedUncertain,
            Assert.Single(expired.Items).Disposition);

        var newerClaim = await ingestion.ClaimNextAsync(
            WorkerInstanceId.New(), TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.True(newerClaim.Claimed);
        Assert.Equal(oldExchange.JobId, newerClaim.Job!.Id);
        var newExchange = await AuthorizeMapClaimAsync(
            ingestion, newerClaim);
        Assert.True(
            newExchange.FenceToken.Value > oldExchange.FenceToken.Value);

        var newerCapture = await CaptureMapAttemptAsync(
            evidence, newExchange, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(13), CreateStaticTextPayload(3, true));
        Assert.Equal(CaptureStatus.CapturedCurrent, newerCapture.Status);
        var newerFetch = Assert.IsType<FetchDescriptor>(
            newerCapture.Fetch).Id;

        // Completion order is intentionally opposite to source chronology:
        // the old response must not take authority, despite arriving last.
        var lateCapture = await CaptureMapAttemptAsync(
            evidence, oldExchange, staticEndpoint.Resource.Id,
            warBAt.AddSeconds(12), CreateStaticTextPayload(4, true));
        Assert.Equal(CaptureStatus.CapturedLate, lateCapture.Status);
        var lateFetch = Assert.IsType<FetchDescriptor>(
            lateCapture.Fetch).Id;

        // Both same-shard cross-endpoint and cross-shard 304 prior bindings
        // must be rejected by the real evidence store before any Fetch insert.
        var foreignShard = await registry.RegisterShardAsync(
            source.Resource.Id, "live-2", "Live-2", "live",
            TestContext.Current.CancellationToken);
        var foreignStaticEndpoint = await registry.RegisterEndpointAsync(
            foreignShard.Resource.Id,
            WarApiCapabilities.StaticMapState.Key,
            "map-static/DeadLandsHex",
            TestContext.Current.CancellationToken);
        var wrongEndpointAttempt = await RejectForeignMapPriorAsync(
            dataSource, ingestion, evidence, dynamicEndpoint.Resource.Id,
            staticChanged, warBAt.AddSeconds(14),
            "m6-h1e-wrong-endpoint-304");
        var wrongShardAttempt = await RejectForeignMapPriorAsync(
            dataSource, ingestion, evidence,
            foreignStaticEndpoint.Resource.Id,
            staticChanged, warBAt.AddSeconds(15),
            "m6-h1e-wrong-shard-304");

        var sourceFetchCount = await CountAsync(dataSource, "evidence.fetches");
        var sourcePayloadCount =
            await CountAsync(dataSource, "evidence.payloads");
        var h1V1 = await M6CompletionConvergence.RunAsync(
            ct => CreateRecoveryCoordinator(dataSource).RunOnceAsync(ct),
            ct => CreateQualityRecoveryCoordinator(dataSource).RunOnceAsync(
                policyV1Target, ct),
            TestContext.Current.CancellationToken);
        Assert.InRange(h1V1, 2, 32);
        var h1V2 = await M6CompletionConvergence.RunAsync(
            ct => CreateRecoveryCoordinator(dataSource).RunOnceAsync(ct),
            ct => CreateQualityRecoveryCoordinator(dataSource).RunOnceAsync(
                target, ct),
            TestContext.Current.CancellationToken);
        Assert.InRange(h1V2, 2, 32);
        Assert.Equal(sourceFetchCount, await CountAsync(
            dataSource, "evidence.fetches"));
        Assert.Equal(sourcePayloadCount, await CountAsync(
            dataSource, "evidence.payloads"));

        Assert.Equal(1L, await CountCoverageStateAsync(
            dataSource, "source_unavailable"));
        Assert.Equal(1L, await CountCoverageStateAsync(
            dataSource, "rejected"));

        await AssertCoverageForFetchAsync(
            dataSource, bodyFailure, "collector_unavailable",
            "body_read_failed");
        await AssertCoverageForFetchAsync(
            dataSource, orphan304, "unknown", "orphan_304");
        await AssertCoverageForAttemptAsync(
            dataSource, abandonedAttempt, "collector_unavailable",
            "abandoned_before_exchange");
        await AssertCoverageForAttemptAsync(
            dataSource, uncertainAttempt, "unknown",
            "uncertain_exchange");
        await AssertCoverageForFetchAsync(
            dataSource, lateFetch, "unknown", "captured_late");
        await AssertCoverageForAttemptAsync(
            dataSource, wrongEndpointAttempt, "unknown",
            "uncertain_exchange");
        await AssertCoverageForAttemptAsync(
            dataSource, wrongShardAttempt, "unknown",
            "uncertain_exchange");

        Assert.Null(await ReadObservationIdAsync(
            dataSource, lateFetch, WarApiVersions.MapQualityPolicyV1));
        Assert.Null(await ReadObservationIdAsync(
            dataSource, lateFetch, WarApiVersions.MapQualityPolicyV2));
        await AssertQualityAsync(
            dataSource, newerFetch, MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertNoFetchForAttemptAsync(
            dataSource, wrongEndpointAttempt);
        await AssertNoFetchForAttemptAsync(
            dataSource, wrongShardAttempt);

        Assert.Null(await ReadObservationIdAsync(
            dataSource, bodyFailure, WarApiVersions.MapQualityPolicyV2));
        Assert.Null(await ReadObservationIdAsync(
            dataSource, orphan304, WarApiVersions.MapQualityPolicyV2));
        Assert.Null(await ReadObservationIdAsync(
            dataSource, unavailable, WarApiVersions.MapQualityPolicyV2));
        Assert.Null(await ReadObservationIdAsync(
            dataSource, rejected, WarApiVersions.MapQualityPolicyV2));
        // A 503 or a rejected 200 is a terminal coverage boundary,
        // NOT a quality candidate of any immutable policy version.
        await using (var absentQuality = dataSource.CreateCommand(
                         """
                         SELECT COUNT(*)
                         FROM quality.map_quality_runs
                         WHERE validation_fetch_id IN
                             (@unavailable, @rejected, @body_failure, @orphan,
                              @late_fetch);
                         """))
        {
            absentQuality.Parameters.AddWithValue(
                "unavailable", unavailable.Value);
            absentQuality.Parameters.AddWithValue(
                "rejected", rejected.Value);
            absentQuality.Parameters.AddWithValue(
                "body_failure", bodyFailure.Value);
            absentQuality.Parameters.AddWithValue(
                "orphan", orphan304.Value);
            absentQuality.Parameters.AddWithValue(
                "late_fetch", lateFetch.Value);
            Assert.Equal(0L, (long)(await absentQuality.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }

        // Raw source text occurrences must never be de-duplicated even when
        // text, marker type and coordinates are byte-for-byte identical.
        await using (var duplicates = dataSource.CreateCommand(
                         """
                         SELECT COUNT(*)
                         FROM evidence.map_text_occurrences AS item
                         JOIN evidence.map_snapshots AS snapshot
                           ON snapshot.id = item.map_snapshot_id
                         WHERE snapshot.representation_fetch_id = @fetch
                           AND item.text = 'Deadlands';
                         """))
        {
            duplicates.Parameters.AddWithValue("fetch", staticInitial.Value);
            Assert.Equal(2L, (long)(await duplicates.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }

        await AssertQualityAsync(
            dataSource, staticInitial, MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertQualityAsync(
            dataSource, staticChanged, MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        await AssertQualityAsync(
            dataSource, static304, MapQualityDecision.Accepted,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(await ReadObservationIdAsync(
            dataSource, static304, WarApiVersions.MapQualityPolicyV2));
        Assert.True(await CountAsync(
            dataSource, "evidence.map_text_occurrences") >= 4);
        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource, WarApiVersions.MapQualityPolicyV1,
            TestContext.Current.CancellationToken);
        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource, WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        // M6-H5-H8: compare persisted semantic content, not regenerated IDs.
        // This fixture is isolated: deletion below must never be used on live
        // storage. Keep all captured source, coverage, parse and M5 context.
        var semanticBefore = await M6SemanticRebuildProof.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        Assert.NotEqual("[]", semanticBefore["snapshots"]);
        Assert.NotEqual("[]", semanticBefore["items"]);
        Assert.NotEqual("[]", semanticBefore["text_items"]);
        Assert.Contains("\"static\"", semanticBefore["snapshots"]);
        Assert.Contains("Deadlands", semanticBefore["text_items"]);
        Assert.Contains("Front line", semanticBefore["text_items"]);
        Assert.NotEqual("[]", semanticBefore["quality_runs"]);
        Assert.NotEqual("[]", semanticBefore["quality_findings"]);
        Assert.NotEqual("[]", semanticBefore["observations"]);
        var authorityBefore = await M6SemanticRebuildProof.ReadAuthorityAsync(
            dataSource, TestContext.Current.CancellationToken);

        await M6SemanticRebuildProof.DeleteDerivedAsync(
            dataSource, TestContext.Current.CancellationToken);
        var deleted = await M6SemanticRebuildProof.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        foreach (var relation in deleted)
        {
            Assert.Equal("[]", relation.Value);
        }
        var missing = await M6CompletionChronologyAudit.ReadAsync(
            dataSource, WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        Assert.True(missing["missing_normalization"] > 0);
        Assert.Equal(
            authorityBefore,
            await M6SemanticRebuildProof.ReadAuthorityAsync(
                dataSource, TestContext.Current.CancellationToken));

        // Replay historical @1 first, then activated @2, using only
        // existing Fetch/Payload/Parse/WarRegion state and recovery code.
        var rebuiltV1 = await M6CompletionConvergence.RunAsync(
            ct => CreateRecoveryCoordinator(dataSource).RunOnceAsync(ct),
            ct => CreateQualityRecoveryCoordinator(dataSource).RunOnceAsync(
                policyV1Target, ct),
            TestContext.Current.CancellationToken);
        Assert.InRange(rebuiltV1, 2, 32);

        var rebuiltV2 = await M6CompletionConvergence.RunAsync(
            ct => CreateRecoveryCoordinator(dataSource).RunOnceAsync(ct),
            ct => CreateQualityRecoveryCoordinator(dataSource).RunOnceAsync(
                target, ct),
            TestContext.Current.CancellationToken);
        Assert.InRange(rebuiltV2, 2, 32);

        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource, WarApiVersions.MapQualityPolicyV1,
            TestContext.Current.CancellationToken);
        await M6CompletionChronologyAudit.AssertCleanAsync(
            dataSource, WarApiVersions.MapQualityPolicyV2,
            TestContext.Current.CancellationToken);
        await M6CompletionProvenanceAudit.AssertCleanAsync(
            dataSource, TestContext.Current.CancellationToken);

        var semanticAfter = await M6SemanticRebuildProof.ReadAsync(
            dataSource, TestContext.Current.CancellationToken);
        M6SemanticRebuildProof.AssertEquivalent(
            semanticBefore, semanticAfter);
        Assert.Equal(
            authorityBefore,
            await M6SemanticRebuildProof.ReadAuthorityAsync(
                dataSource, TestContext.Current.CancellationToken));

        Assert.Equal(
            WarApiVersions.MapQualityPolicyV2,
            WarApiVersions.MapQualityPolicy);
    }

    [Fact]
    public async Task LaterQualityCandidateDefersUntilEarlierGapRecovers()
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
        var at = new DateTimeOffset(
            2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            at,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g9-order-war","warNumber":138,"winner":"NONE"}"""),
            "m6-g9-order-war");
        await CaptureRawAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            at.AddSeconds(1),
            Encoding.UTF8.GetBytes(
                """["DeadLandsHex"]"""),
            "m6-g9-order-maps");
        var earlier = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(2),
            CreateDynamicPayload(
                version: 1,
                lastUpdated: 1_700_001_000_000,
                Array.Empty<QualityMapItem>()),
            "m6-g9-order-earlier");
        var later = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(3),
            CreateDynamicPayload(
                version: 2,
                lastUpdated: 1_700_001_001_000,
                Array.Empty<QualityMapItem>()),
            "m6-g9-order-later");

        var coverage = await CreateRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, coverage.ParseRunsRepaired);
        Assert.Equal(4, coverage.CanonicalCompleted);

        var fetchCount =
            await CountAsync(dataSource, "evidence.fetches");

        var laterSnapshot = await ReadSnapshotForRepresentationAsync(
            dataSource,
            later);
        var premature = await CreateQualityCoordinator(dataSource)
            .EvaluateAsync(
                laterSnapshot,
                shard.Resource.Id,
                later,
                at.AddSeconds(3),
                TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiMapQualityStatus.Deferred,
            premature.Status);
        Assert.Equal(
            "earlier_quality_missing",
            premature.DeferredReason);
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "quality.map_quality_runs"));

        var recovered = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, recovered.TerminalCompleted);
        Assert.Equal(0, recovered.Deferred);
        Assert.Equal(0, recovered.VersionBlocked);

        var earlierObservation = await ReadObservationIdAsync(
            dataSource,
            earlier,
            WarApiVersions.MapQualityPolicyV2);
        Assert.NotNull(earlierObservation);
        Assert.Equal(
            earlierObservation,
            await ReadBaselineObservationIdAsync(
                dataSource,
                later,
                WarApiVersions.MapQualityPolicyV2));

        Assert.Equal(
            fetchCount,
            await CountAsync(dataSource, "evidence.fetches"));

        var replay = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.ProgressCount);
        Assert.Equal(0, replay.OutstandingCount);
    }

    [Fact]
    public async Task InterruptedQualityTransactionRollsBackAndRecoveryRetries()
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
        var at = new DateTimeOffset(
            2026, 10, 14, 11, 0, 0, TimeSpan.Zero);

        await CaptureRawAsync(
            ingestion,
            evidence,
            warEndpoint.Resource.Id,
            at,
            Encoding.UTF8.GetBytes(
                """{"warId":"m6-g9-tx-war","warNumber":139,"winner":"NONE"}"""),
            "m6-g9-tx-war");
        await CaptureRawAsync(
            ingestion,
            evidence,
            mapsEndpoint.Resource.Id,
            at.AddSeconds(1),
            Encoding.UTF8.GetBytes(
                """["DeadLandsHex"]"""),
            "m6-g9-tx-maps");
        var validation = await CaptureRawAsync(
            ingestion,
            evidence,
            dynamicEndpoint.Resource.Id,
            at.AddSeconds(2),
            CreateDynamicPayload(
                version: 1,
                lastUpdated: 1_700_002_000_000,
                [new QualityMapItem(
                    "WARDENS",
                    39,
                    0.5,
                    0.5,
                    0)]),
            "m6-g9-tx-map");

        _ = await CreateRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);

        var fetchCount =
            await CountAsync(dataSource, "evidence.fetches");
        var snapshotCount =
            await CountAsync(dataSource, "evidence.map_snapshots");

        await using (var inject = dataSource.CreateCommand(
                         """
                         CREATE OR REPLACE FUNCTION
                             public.m6_g9_fail_map_observation()
                         RETURNS trigger
                         LANGUAGE plpgsql
                         AS $m6g9$
                         BEGIN
                             RAISE EXCEPTION
                                 'm6-g9 injected map observation failure';
                         END;
                         $m6g9$;

                         DROP TRIGGER IF EXISTS
                             m6_g9_fail_map_observation
                             ON runtime.map_observations;

                         CREATE TRIGGER m6_g9_fail_map_observation
                         BEFORE INSERT ON runtime.map_observations
                         FOR EACH ROW
                         EXECUTE FUNCTION
                             public.m6_g9_fail_map_observation();
                         """))
        {
            await inject.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        try
        {
            await Assert.ThrowsAsync<PostgresException>(
                async () =>
                    await CreateQualityRecoveryCoordinator(dataSource)
                        .RunOnceAsync(
                            TestContext.Current.CancellationToken));
        }
        finally
        {
            await using var clear = dataSource.CreateCommand(
                """
                DROP TRIGGER IF EXISTS
                    m6_g9_fail_map_observation
                    ON runtime.map_observations;
                DROP FUNCTION IF EXISTS
                    public.m6_g9_fail_map_observation();
                """);
            await clear.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        // The injected failure occurs after the QualityRun insert attempt.
        // The transaction must leave no partial durable quality graph.
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "quality.map_quality_runs"));
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "quality.map_quality_findings"));
        Assert.Equal(
            0L,
            await CountAsync(dataSource, "runtime.map_observations"));

        await using (var region = dataSource.CreateCommand(
                         """
                         SELECT source_region_id
                         FROM runtime.war_regions
                         WHERE source_map_name = 'DeadLandsHex';
                         """))
        {
            var sourceRegionId = await region.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            Assert.True(sourceRegionId is null or DBNull);
        }

        var retry = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, retry.TerminalCompleted);
        Assert.Equal(0, retry.Deferred);
        Assert.Equal(0, retry.VersionBlocked);

        Assert.Equal(
            1L,
            await CountAsync(dataSource, "quality.map_quality_runs"));
        Assert.Equal(
            1L,
            await CountAsync(dataSource, "runtime.map_observations"));
        Assert.NotNull(
            await ReadObservationIdAsync(
                dataSource,
                validation,
                WarApiVersions.MapQualityPolicyV2));

        await using (var region = dataSource.CreateCommand(
                         """
                         SELECT source_region_id
                         FROM runtime.war_regions
                         WHERE source_map_name = 'DeadLandsHex';
                         """))
        {
            Assert.Equal(
                1,
                Assert.IsType<int>(
                    await region.ExecuteScalarAsync(
                        TestContext.Current.CancellationToken)));
        }

        Assert.Equal(
            fetchCount,
            await CountAsync(dataSource, "evidence.fetches"));
        Assert.Equal(
            snapshotCount,
            await CountAsync(dataSource, "evidence.map_snapshots"));

        var replay = await CreateQualityRecoveryCoordinator(dataSource)
            .RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.ProgressCount);
        Assert.Equal(0, replay.OutstandingCount);
    }

    private sealed record QualityMapItem(
        string TeamId,
        int IconType,
        double X,
        double Y,
        int Flags);

    private static byte[] CreateDynamicPayload(
        int version,
        long lastUpdated,
        IReadOnlyList<QualityMapItem> items)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                regionId = 1,
                scorchedVictoryTowns = 0,
                mapItems = items.Select(
                    item => new
                    {
                        teamId = item.TeamId,
                        iconType = item.IconType,
                        x = item.X,
                        y = item.Y,
                        flags = item.Flags,
                    }),
                mapTextItems = Array.Empty<object>(),
                lastUpdated,
                version,
            });
    }

    // Raw static map payloads deliberately preserve a repeated text item;
    // H1/H5 must compare multiplicity and ordinal, not deduplicate labels.
    private static byte[] CreateStaticTextPayload(
        int version, bool changed)
    {
        var items = changed
            ? new[]
            {
                new { teamId = "WARDENS", iconType = 17,
                      x = 0.25, y = 0.75, flags = 0 },
                new { teamId = "COLONIALS", iconType = 17,
                      x = 0.68, y = 0.35, flags = 0 },
            }
            : new[]
            {
                new { teamId = "WARDENS", iconType = 17,
                      x = 0.25, y = 0.75, flags = 0 },
            };
        var textItems = changed
            ? new[]
            {
                new { text = "Deadlands", x = 0.5, y = 0.5,
                      mapMarkerType = "Major" },
                new { text = "Front line", x = 0.6, y = 0.4,
                      mapMarkerType = "Major" },
            }
            : new[]
            {
                new { text = "Deadlands", x = 0.5, y = 0.5,
                      mapMarkerType = "Major" },
                new { text = "Deadlands", x = 0.5, y = 0.5,
                      mapMarkerType = "Major" },
            };
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                regionId = 1,
                scorchedVictoryTowns = 0,
                mapItems = items,
                mapTextItems = textItems,
                lastUpdated = 1_700_000_100_000L + version * 10_000L,
                version,
            });
    }

    private static QualityMapItem[] NeutralItems() =>
        [
            new("NONE", 23, 0.25, 0.35, 0),
            new("NONE", 24, 0.35, 0.45, 0),
            new("NONE", 45, 0.45, 0.55, 0),
            new("NONE", 46, 0.55, 0.65, 0),
        ];

    private static QualityMapItem[] HealthyItems() =>
        [
            new("WARDENS", 39, 0.50, 0.56, 0),
            new("COLONIALS", 39, 0.67, 0.38, 0),
            new("WARDENS", 11, 0.39, 0.29, 0),
            new("COLONIALS", 34, 0.68, 0.39, 0),
            new("WARDENS", 33, 0.75, 0.52, 0),
            new("COLONIALS", 33, 0.36, 0.25, 0),
            new("WARDENS", 18, 0.53, 0.58, 0),
            new("COLONIALS", 18, 0.40, 0.21, 0),
            new("WARDENS", 18, 0.75, 0.44, 0),
            new("COLONIALS", 18, 0.69, 0.33, 0),
            new("WARDENS", 18, 0.74, 0.39, 0),
            new("COLONIALS", 52, 0.55, 0.55, 0),
            new("WARDENS", 53, 0.52, 0.54, 0),
            new("COLONIALS", 56, 0.38, 0.30, 0),
            new("WARDENS", 56, 0.78, 0.52, 0),
            new("COLONIALS", 56, 0.70, 0.38, 0),
            new("WARDENS", 56, 0.46, 0.52, 0),
            new("COLONIALS", 56, 0.39, 0.73, 0),
            new("WARDENS", 45, 0.21, 0.31, 0),
            new("COLONIALS", 46, 0.22, 0.32, 0),
            new("WARDENS", 47, 0.23, 0.33, 0),
            new("COLONIALS", 51, 0.24, 0.34, 0),
            new("NONE", 23, 0.25, 0.35, 0),
            new("NONE", 24, 0.26, 0.36, 0),
        ];

    private static QualityMapItem[] UnknownTaxonomyItems()
    {
        var items = HealthyItems();
        items[0] = items[0] with { TeamId = "FUTURE_FACTION" };
        items[1] = items[1] with { IconType = 97 };
        items[2] = items[2] with { Flags = 1 << 30 };
        return items;
    }

    private static async Task<MapSnapshotResult>
        ReadSnapshotForRepresentationAsync(
            NpgsqlDataSource dataSource,
            FetchId representationFetchId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT normalization_run_id
            FROM evidence.map_snapshots
            WHERE representation_fetch_id = @representation_fetch_id;
            """);
        command.Parameters.AddWithValue(
            "representation_fetch_id",
            representationFetchId.Value);

        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        var normalizationRunId = new NormalizationRunId(
            Assert.IsType<Guid>(value));

        return await new MapSnapshotKernel(
                new PostgresMapSnapshotStore(dataSource))
            .GetByNormalizationRunAsync(
                normalizationRunId,
                TestContext.Current.CancellationToken)
            ?? throw new Xunit.Sdk.XunitException(
                "Expected durable map snapshot was not found.");
    }

    private static async Task<string> ReadPolicyQualitySignatureAsync(
        NpgsqlDataSource dataSource,
        string policyVersion)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COALESCE(
                string_agg(
                    run.id::text || '|' ||
                    run.validation_fetch_id::text || '|' ||
                    run.decision || '|' ||
                    COALESCE(
                        run.baseline_map_observation_id::text,
                        'NULL'),
                    ',' ORDER BY validation.retrieved_at,
                               validation.id),
                '')
            FROM quality.map_quality_runs AS run
            JOIN evidence.fetches AS validation
                ON validation.id = run.validation_fetch_id
            WHERE run.quality_policy_version = @policy_version;
            """);
        command.Parameters.AddWithValue(
            "policy_version",
            policyVersion);

        return (string)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task AssertQualityAsync(
        NpgsqlDataSource dataSource,
        FetchId validationFetchId,
        MapQualityDecision expectedDecision,
        string policyVersion,
        params string[] requiredRuleKeys)
    {
        await using (var command = dataSource.CreateCommand(
                         """
                         SELECT decision
                         FROM quality.map_quality_runs
                         WHERE validation_fetch_id =
                                   @validation_fetch_id
                           AND quality_policy_version =
                                   @policy_version;
                         """))
        {
            command.Parameters.AddWithValue(
                "validation_fetch_id",
                validationFetchId.Value);
            command.Parameters.AddWithValue(
                "policy_version",
                policyVersion);

            Assert.Equal(
                expectedDecision.ToString().ToLowerInvariant(),
                Assert.IsType<string>(
                    await command.ExecuteScalarAsync(
                        TestContext.Current.CancellationToken)));
        }

        await using var findings = dataSource.CreateCommand(
            """
            SELECT finding.rule_key
            FROM quality.map_quality_findings AS finding
            JOIN quality.map_quality_runs AS run
                ON run.id = finding.quality_run_id
            WHERE run.validation_fetch_id = @validation_fetch_id
              AND run.quality_policy_version = @policy_version
            ORDER BY finding.rule_key;
            """);
        findings.Parameters.AddWithValue(
            "validation_fetch_id",
            validationFetchId.Value);
        findings.Parameters.AddWithValue(
            "policy_version",
            policyVersion);

        var actualRuleKeys = new List<string>();
        await using var reader = await findings.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(
                   TestContext.Current.CancellationToken))
        {
            actualRuleKeys.Add(reader.GetString(0));
        }

        foreach (var requiredRuleKey in requiredRuleKeys)
        {
            Assert.Contains(requiredRuleKey, actualRuleKeys);
        }
    }

    private static async Task<Guid?> ReadObservationIdAsync(
        NpgsqlDataSource dataSource,
        FetchId validationFetchId,
        string policyVersion)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT observation.id
            FROM runtime.map_observations AS observation
            JOIN quality.map_quality_runs AS run
                ON run.id = observation.quality_run_id
            WHERE run.validation_fetch_id = @validation_fetch_id
              AND run.quality_policy_version = @policy_version;
            """);
        command.Parameters.AddWithValue(
            "validation_fetch_id",
            validationFetchId.Value);
        command.Parameters.AddWithValue(
            "policy_version",
            policyVersion);

        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<Guid?> ReadBaselineObservationIdAsync(
        NpgsqlDataSource dataSource,
        FetchId validationFetchId,
        string policyVersion)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT baseline_map_observation_id
            FROM quality.map_quality_runs
            WHERE validation_fetch_id = @validation_fetch_id
              AND quality_policy_version = @policy_version;
            """);
        command.Parameters.AddWithValue(
            "validation_fetch_id",
            validationFetchId.Value);
        command.Parameters.AddWithValue(
            "policy_version",
            policyVersion);

        var value = await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : (Guid)value;
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
        return new WarApiMapQualityRecoveryCoordinator(
            new PostgresMapQualityGapReader(dataSource),
            new MapSnapshotKernel(
                new PostgresMapSnapshotStore(dataSource)),
            CreateQualityCoordinator(dataSource));
    }

    private static WarApiMapQualityCoordinator CreateQualityCoordinator(
        NpgsqlDataSource dataSource)
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

        return new WarApiMapQualityCoordinator(
            mapContext,
            new MapSnapshotKernel(
                new PostgresMapSnapshotStore(dataSource)),
            new PostgresMapQualityOrderingReader(dataSource),
            qualityStore,
            new MapQualityKernel(qualityStore),
            TimeProvider.System);
    }

    private static async Task<FetchId> CaptureRawAsync(
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        FoxData.Core.Sources.EndpointId endpointId,
        DateTimeOffset retrievedAt,
        byte[]? body,
        string jobKey = "m5-h-recovery",
        int statusCode = 200,
        string? bodyErrorCode = null)
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
                statusCode,
                body is null ? null : "application/json",
                null,
                body?.LongLength,
                "\"m5-h\"",
                "max-age=60",
                retrievedAt.AddMinutes(1),
                2,
                BodyErrorCode: bodyErrorCode),
            body is null ? null : new ReadOnlyMemory<byte>(body),
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
        FetchId? priorFetchId,
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


    // H1-D test-only helper: use the real ingestion kernel and recovery
    // transition to produce durable terminal Attempts without a Fetch.
    private static async Task<IngestionAttemptId>
        RecoverInterruptedMapAttemptAsync(
            NpgsqlDataSource dataSource,
            IngestionKernel ingestion,
            FoxData.Core.Sources.EndpointId endpointId,
            string jobKey,
            bool authorizeExchange)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var queued = await ingestion.EnqueueAsync(
            endpointId, jobKey, now, now,
            cancellationToken: cancellationToken);
        Assert.Equal(JobEnqueueStatus.Created, queued.Status);

        var workerId = WorkerInstanceId.New();
        var claim = await ingestion.ClaimNextAsync(
            workerId, TimeSpan.FromMinutes(5), cancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId, claim.Job!.Id, workerId,
            claim.Job.LeaseGeneration, cancellationToken);
        Assert.Equal(BeginAttemptStatus.Started, begun.Status);

        var fenced = await ingestion.AcquireEndpointFenceAsync(
            attemptId, workerId, claim.Job.LeaseGeneration,
            cancellationToken);
        Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

        if (authorizeExchange)
        {
            var authorization = await ingestion.AuthorizeExchangeAsync(
                attemptId, workerId, claim.Job.LeaseGeneration,
                cancellationToken);
            Assert.Equal(
                ExchangeAuthorizationStatus.AuthorizedNow,
                authorization.Status);
        }

        await using (var expire = dataSource.CreateCommand(
                         """
                         UPDATE ingest.collection_jobs
                         SET lease_expires_at =
                             transaction_timestamp() - interval '1 second'
                         WHERE id = @job_id;
                         """))
        {
            expire.Parameters.AddWithValue("job_id", claim.Job.Id.Value);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync(
                cancellationToken));
        }

        var recovery = new IngestionRecovery(
            new PostgresIngestionRecoveryStore(dataSource),
            new IngestionRecoveryLimits(batchSize: 8));
        var recovered = await recovery.RecoverExpiredAsync(cancellationToken);
        var item = Assert.Single(recovered.Items);
        Assert.Equal(attemptId, item.AttemptId);
        Assert.Equal(
            authorizeExchange
                ? RecoveryDisposition.MarkedUncertain
                : RecoveryDisposition.RequeuedBeforeExchange,
            item.Disposition);

        // Both M2 recovery dispositions legitimately requeue the job.
        // Keep it out of the next synthetic ClaimNext sweep so each fault
        // fixture creates a distinct attempt without issuing another HTTP
        // exchange or disguising the pending job as completed.
        await using (var defer = dataSource.CreateCommand(
                         """
                         UPDATE ingest.collection_jobs
                         SET available_at =
                             transaction_timestamp() + interval '1 day'
                         WHERE id = @job_id AND state = 'pending';
                         """))
        {
            defer.Parameters.AddWithValue("job_id", claim.Job.Id.Value);
            Assert.Equal(1, await defer.ExecuteNonQueryAsync(
                cancellationToken));
        }

        return attemptId;
    }

    private static async Task AssertCoverageForFetchAsync(
        NpgsqlDataSource dataSource,
        FetchId fetchId,
        string expectedState,
        string expectedDetail)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM evidence.coverage_observations AS coverage
            WHERE coverage.validation_fetch_id = @fetch_id
              AND coverage.state = @state
              AND coverage.detail_code = @detail;
            """);
        command.Parameters.AddWithValue("fetch_id", fetchId.Value);
        command.Parameters.AddWithValue("state", expectedState);
        command.Parameters.AddWithValue("detail", expectedDetail);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);
    }

    private static async Task AssertCoverageForAttemptAsync(
        NpgsqlDataSource dataSource,
        IngestionAttemptId attemptId,
        string expectedState,
        string expectedDetail)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM evidence.coverage_observations AS coverage
            WHERE coverage.attempt_id = @attempt_id
              AND coverage.validation_fetch_id IS NULL
              AND coverage.representation_fetch_id IS NULL
              AND coverage.source_parse_run_id IS NULL
              AND coverage.state = @state
              AND coverage.detail_code = @detail;
            """);
        command.Parameters.AddWithValue("attempt_id", attemptId.Value);
        command.Parameters.AddWithValue("state", expectedState);
        command.Parameters.AddWithValue("detail", expectedDetail);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!);
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
