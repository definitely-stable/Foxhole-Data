using System.Text;
using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Runtime;
using FoxData.Infrastructure.Canonical;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M5WarReportNormalizationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReportNormalizationBuildsMissingWarAndRegionContextFromDurableEvidence()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 14, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-war",
            """{"warId":"war-report-129","warNumber":129,"winner":"NONE"}""",
            start);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-maps",
            """["DeadLandsHex","MarbanHollow"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-deadlands",
            """
            {
              "totalEnlistments":148,
              "colonialCasualties":202,
              "wardenCasualties":222,
              "dayOfWar":2
            }
            """,
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);

        var canonical = result.Canonical;
        Assert.Equal("DeadLandsHex", canonical.WarRegion.SourceMapName);
        Assert.Equal(start.AddMinutes(1), canonical.WarRegion.FirstSeenAt);
        Assert.Equal(start.AddMinutes(1), canonical.WarRegion.LastSeenAt);
        Assert.Equal(start.AddMinutes(2), canonical.Observation.ObservedAt);
        Assert.Equal(148L, canonical.Observation.TotalEnlistments);
        Assert.Equal(202L, canonical.Observation.ColonialCasualties);
        Assert.Equal(222L, canonical.Observation.WardenCasualties);
        Assert.Equal(2, canonical.Observation.DayOfWar);
        Assert.Equal(
            report.RepresentationFetchId,
            canonical.Observation.RepresentationFetchId);

        Assert.Equal(1L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_regions"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
    }

    [Fact]
    public async Task ReportReplayReturnsSameImmutableObservation()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 15, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-replay",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-replay",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":4}""",
            start.AddMinutes(2));

        var first = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);
        var replay = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            first.Status);
        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            replay.Status);
        Assert.NotNull(first.Canonical);
        Assert.NotNull(replay.Canonical);
        Assert.Equal(
            first.NormalizationRun!.Id,
            replay.NormalizationRun!.Id);
        Assert.Equal(
            first.Canonical.Observation.Id,
            replay.Canonical.Observation.Id);
        Assert.Equal(
            first.Canonical.WarRegion.Id,
            replay.Canonical.WarRegion.Id);
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
    }

    [Fact]
    public async Task NegativeReportCounterRejectsWithoutCanonicalObservation()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 16, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-negative",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-negative",
            """{"totalEnlistments":10,"colonialCasualties":-1,"wardenCasualties":30,"dayOfWar":4}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Rejected,
            result.Status);
        Assert.Equal(
            "negative_colonial_casualties",
            result.NormalizationRun!.ErrorCode);
        Assert.Null(result.Canonical);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task UnknownReportPropertiesRemainRepresentable()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 17, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-unknown",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-unknown",
            """{"totalEnlistments":null,"colonialCasualties":20,"wardenCasualties":null,"dayOfWar":5,"futureField":"kept-in-evidence"}""",
            start.AddMinutes(2));

        Assert.Equal("parsed_with_unknowns", report.Outcome);

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);
        Assert.Null(result.Canonical.Observation.TotalEnlistments);
        Assert.Equal(20L, result.Canonical.Observation.ColonialCasualties);
        Assert.Null(result.Canonical.Observation.WardenCasualties);
        Assert.Equal(5, result.Canonical.Observation.DayOfWar);
    }

    [Fact]
    public async Task ReportNeverExtendsWarRegionMembershipBounds()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-bounds",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-bounds",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddHours(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Canonical);
        Assert.Equal(
            start.AddMinutes(1),
            result.Canonical.WarRegion.FirstSeenAt);
        Assert.Equal(
            start.AddMinutes(1),
            result.Canonical.WarRegion.LastSeenAt);

        var stored = await fixture.ReadWarRegionAsync(
            result.Canonical.WarRegion.Id);
        Assert.Equal(start.AddMinutes(1), stored.FirstSeenAt);
        Assert.Equal(start.AddMinutes(1), stored.LastSeenAt);
    }

    [Fact]
    public async Task MapAbsentFromLatestListDefersReportWithoutBurningIdentity()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 19, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-map-absent-war",
            """{"warId":"war-map-absent","warNumber":129,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-map-absent-maps",
            """["MarbanHollow"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-map-absent-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_membership_unconfirmed",
            result.DeferredReason);
        Assert.Null(result.NormalizationRun);
        Assert.Equal(
            0L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task OldMapRepresentationDoesNotBindReportToNewWar()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 20, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-old-war",
            """{"warId":"old-war","warNumber":129,"winner":"WARDENS"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-old-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-new-war",
            """{"warId":"new-war","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(10));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-new-war-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(11));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_war_context_mismatch",
            result.DeferredReason);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task CrossWarMap304DefersToCoverageInsteadOfInventingMembership()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 21, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-304-old-war",
            """{"warId":"old-war-304","warNumber":129,"winner":"WARDENS"}""",
            start);
        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-304-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-304-new-war",
            """{"warId":"new-war-304","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(10));

        await fixture.CreateValidation304Async(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-304-validation",
            maps.RepresentationFetchId,
            start.AddMinutes(10).AddSeconds(30));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-304-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(11));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_continuity_requires_coverage",
            result.DeferredReason);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task ExactCaseSensitiveMapIdentitySelectsMatchingWarRegion()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 22, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-case-war",
            """{"warId":"case-war","warNumber":129,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-case-maps",
            """["DeadLandsHex","deadlandshex"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("deadlandshex"),
            "war-report-case-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);
        Assert.Equal(
            "deadlandshex",
            result.Canonical.WarRegion.SourceMapName);
        Assert.Equal(
            2L,
            await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task LatestWarAndMapParsesCanBeNormalizedOnDemand()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 23, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-on-demand-war",
            """{"warId":"on-demand-war","warNumber":130,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-on-demand-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));

        Assert.Equal(0L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_regions"));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-on-demand-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.Equal(1L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(1L, await fixture.CountAsync("runtime.war_regions"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
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
        var canonicalEvidence =
            new PostgresCanonicalEvidenceReader(dataSource);
        var options = CreateOptions();
        var sourceContextReader =
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
                sourceContextReader,
                warNormalization,
                new RegionCanonicalKernel(
                    new PostgresRegionCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var reportNormalization =
            new WarApiWarReportNormalizationCoordinator(
                canonicalEvidence,
                sourceContextReader,
                new PostgresWarRegionReader(dataSource),
                warNormalization,
                regionNormalization,
                new WarReportCanonicalKernel(
                    new PostgresWarReportCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);

        return new Fixture(
            dataSource,
            registry,
            source.Resource.Id,
            new IngestionKernel(
                new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(
                new PostgresEvidenceKernelStore(dataSource)),
            new PostgresSourceParseRunStore(dataSource),
            reportNormalization);
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
            OutboundGlobalMinimumInterval:
                TimeSpan.FromMilliseconds(150),
            OutboundPerHostMinimumInterval:
                TimeSpan.FromMilliseconds(400),
            UserAgent: "FoxData-Test/M5-F");

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        FoxData.Core.Sources.SourceId sourceId,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        WarApiWarReportNormalizationCoordinator reportNormalization)
        : IAsyncDisposable
    {
        public WarApiWarReportNormalizationCoordinator ReportNormalization { get; } =
            reportNormalization;

        public async Task CreateBaseContextAsync(
            DateTimeOffset start,
            string keyPrefix,
            string sourceMapName)
        {
            _ = await CreateParsedAsync(
                "live-1",
                WarApiCatalog.War(),
                $"{keyPrefix}-war",
                """{"warId":"base-war","warNumber":129,"winner":"NONE"}""",
                start);
            _ = await CreateParsedAsync(
                "live-1",
                WarApiCatalog.Maps(),
                $"{keyPrefix}-maps",
                $"[\"{sourceMapName}\"]",
                start.AddMinutes(1));
        }

        public async Task<SourceParseRunDescriptor> CreateParsedAsync(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            string json,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var capture = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode: 200,
                Encoding.UTF8.GetBytes(json),
                priorFetchId: null);

            var body = capture.Payload!.Body.ToArray();
            var parsed = new WarApiParser().Parse(
                sourceEndpoint.Capability,
                body);
            Assert.True(parsed.Parsed);

            var parseStartedAt =
                retrievedAt.AddMilliseconds(1);
            return await parseRuns.RecordAsync(
                new SourceParseRunWrite(
                    capture.Fetch!.Id,
                    sourceEndpoint.Capability.Key,
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

        public async Task CreateValidation304Async(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            FetchId priorFetchId,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var capture = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode: 304,
                body: null,
                priorFetchId);

            Assert.Equal(
                CaptureStatus.CapturedCurrent,
                capture.Status);
            Assert.Null(capture.Fetch!.PayloadId);
            Assert.Equal(
                priorFetchId,
                capture.Fetch.PriorFetchId);
        }

        private async Task<CaptureResult> CaptureAsync(
            FoxData.Core.Sources.EndpointId endpointId,
            string idempotencyKey,
            DateTimeOffset retrievedAt,
            int statusCode,
            byte[]? body,
            FetchId? priorFetchId)
        {
            var scheduledAt =
                DateTimeOffset.UtcNow.AddSeconds(-1);
            var queued = await ingestion.EnqueueAsync(
                endpointId,
                idempotencyKey,
                scheduledAt,
                scheduledAt,
                cancellationToken:
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                JobEnqueueStatus.Created,
                queued.Status);

            var workerId = WorkerInstanceId.New();
            var claim = await ingestion.ClaimNextAsync(
                workerId,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.True(claim.Claimed);
            Assert.Equal(
                endpointId,
                claim.Job!.EndpointId);

            var attemptId = IngestionAttemptId.New();
            var begun = await ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job.Id,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                BeginAttemptStatus.Started,
                begun.Status);

            var fenced =
                await ingestion.AcquireEndpointFenceAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                FenceAcquireStatus.AcquiredNow,
                fenced.Status);

            var authorized =
                await ingestion.AuthorizeExchangeAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                ExchangeAuthorizationStatus.AuthorizedNow,
                authorized.Status);

            var capture =
                await evidence.CaptureSourceResponseAsync(
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
                        "application/json",
                        null,
                        body?.LongLength,
                        "\"m5-f\"",
                        "max-age=60",
                        retrievedAt.AddMinutes(1),
                        2),
                    body,
                    priorFetchId,
                    TestContext.Current.CancellationToken);

            Assert.Equal(
                CaptureStatus.CapturedCurrent,
                capture.Status);

            return capture;
        }

        public async Task<WarRegionDescriptor> ReadWarRegionAsync(
            WarRegionId warRegionId)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT
                        id, war_id, region_id,
                        source_map_name, source_region_id,
                        first_seen_at, last_seen_at, created_at
                    FROM runtime.war_regions
                    WHERE id = @id;
                    """);
            command.Parameters.AddWithValue(
                "id",
                warRegionId.Value);

            await using var reader =
                await command.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
            Assert.True(
                await reader.ReadAsync(
                    TestContext.Current.CancellationToken));

            return new WarRegionDescriptor(
                new WarRegionId(reader.GetGuid(0)),
                new WarId(reader.GetGuid(1)),
                new RegionId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.IsDBNull(4)
                    ? null
                    : reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }

        public async Task<long> CountNormalizationRunsAsync(
            string normalizerVersion)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT COUNT(*)
                    FROM evidence.normalization_runs
                    WHERE normalizer_version = @normalizer_version;
                    """);
            command.Parameters.AddWithValue(
                "normalizer_version",
                normalizerVersion);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountAsync(string tableName)
        {
            await using var command =
                dataSource.CreateCommand(
                    $"SELECT COUNT(*) FROM {tableName};");
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() =>
            dataSource.DisposeAsync();
    }
}
