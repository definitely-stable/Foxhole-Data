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
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M5RegionNormalizationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActiveMapListCreatesCaseSensitiveMembershipsAndReplaysIdempotently()
    {
        await using var fixture = await CreateFixtureAsync();
        var warObservedAt = new DateTimeOffset(
            2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
        var mapsObservedAt = warObservedAt.AddMinutes(1);

        var war = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war",
            """{"warId":"war-129","warNumber":129,"winner":"NONE"}""",
            warObservedAt);

        _ = await fixture.WarNormalization.NormalizeAsync(
            war.Id,
            TestContext.Current.CancellationToken);

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps",
            """["DeadLandsHex","deadlandshex","DeadLandsHex"]""",
            mapsObservedAt);

        var first = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);
        var replay = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Normalized,
            first.Status);
        Assert.Equal(
            WarApiRegionNormalizationStatus.Normalized,
            replay.Status);
        Assert.NotNull(first.Canonical);
        Assert.Null(replay.Canonical);
        Assert.Equal(
            first.NormalizationRun!.Id,
            replay.NormalizationRun!.Id);

        var firstMemberships = first.Canonical.Memberships
            .OrderBy(x => x.Membership.SourceMapName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, firstMemberships.Length);
        Assert.Equal(
            ["DeadLandsHex", "deadlandshex"],
            firstMemberships.Select(x => x.Membership.SourceMapName).ToArray());
        Assert.Equal(
            [
                "official-war-api/map/DeadLandsHex",
                "official-war-api/map/deadlandshex",
            ],
            firstMemberships.Select(x => x.Region.CanonicalKey).ToArray());
        Assert.All(
            firstMemberships,
            item =>
            {
                Assert.Equal(mapsObservedAt, item.Membership.FirstSeenAt);
                Assert.Equal(mapsObservedAt, item.Membership.LastSeenAt);
                Assert.Null(item.Membership.SourceRegionId);
            });
        Assert.Equal(2L, await fixture.CountAsync("runtime.regions"));
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task LaterMapObservationExtendsSeenBoundsWithoutDeletingMissingMembership()
    {
        await using var fixture = await CreateFixtureAsync();
        var warObservedAt = new DateTimeOffset(
            2026, 9, 27, 9, 0, 0, TimeSpan.Zero);
        var firstObservedAt = warObservedAt.AddMinutes(1);
        var secondObservedAt = firstObservedAt.AddMinutes(5);

        var war = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-bounds",
            """{"warId":"war-bounds","warNumber":129,"winner":"NONE"}""",
            warObservedAt);
        _ = await fixture.WarNormalization.NormalizeAsync(
            war.Id,
            TestContext.Current.CancellationToken);

        var firstMaps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-bounds-1",
            """["DeadLandsHex","MarbanHollow"]""",
            firstObservedAt);
        _ = await fixture.RegionNormalization.NormalizeAsync(
            firstMaps.Id,
            TestContext.Current.CancellationToken);

        var secondMaps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-bounds-2",
            """["DeadLandsHex","CallahansPassage"]""",
            secondObservedAt);
        _ = await fixture.RegionNormalization.NormalizeAsync(
            secondMaps.Id,
            TestContext.Current.CancellationToken);

        var deadlands = await fixture.ReadWarRegionAsync("DeadLandsHex");
        var marban = await fixture.ReadWarRegionAsync("MarbanHollow");
        var callahans = await fixture.ReadWarRegionAsync("CallahansPassage");

        Assert.Equal(firstObservedAt, deadlands.FirstSeenAt);
        Assert.Equal(secondObservedAt, deadlands.LastSeenAt);
        Assert.Equal(firstObservedAt, marban.FirstSeenAt);
        Assert.Equal(firstObservedAt, marban.LastSeenAt);
        Assert.Equal(secondObservedAt, callahans.FirstSeenAt);
        Assert.Equal(secondObservedAt, callahans.LastSeenAt);
        Assert.Equal(3L, await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task MissingWarContextDefersWithoutBurningNormalizationIdentity()
    {
        await using var fixture = await CreateFixtureAsync();
        var mapsObservedAt = new DateTimeOffset(
            2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-deferred",
            """["DeadLandsHex"]""",
            mapsObservedAt);

        var deferred = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Deferred,
            deferred.Status);
        Assert.Null(deferred.NormalizationRun);
        Assert.Equal(
            "war_context_unavailable",
            deferred.DeferredReason);
        Assert.Equal(
            0L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.RegionNormalizer));
        Assert.Equal(0L, await fixture.CountAsync("runtime.regions"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_regions"));

        var war = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-late-normalization",
            """{"warId":"war-before-maps","warNumber":129,"winner":"NONE"}""",
            mapsObservedAt.AddMinutes(-1));
        _ = await fixture.WarNormalization.NormalizeAsync(
            war.Id,
            TestContext.Current.CancellationToken);

        var recovered = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Normalized,
            recovered.Status);
        Assert.NotNull(recovered.NormalizationRun);
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.RegionNormalizer));
        Assert.Equal(1L, await fixture.CountAsync("runtime.regions"));
        Assert.Equal(1L, await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task FutureWarObservationNeverCapturesEarlierMapEvidence()
    {
        await using var fixture = await CreateFixtureAsync();
        var mapsObservedAt = new DateTimeOffset(
            2026, 9, 27, 11, 0, 0, TimeSpan.Zero);

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-before-war",
            """["DeadLandsHex"]""",
            mapsObservedAt);

        var first = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            WarApiRegionNormalizationStatus.Deferred,
            first.Status);

        var futureWar = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "future-war",
            """{"warId":"future-war","warNumber":130,"winner":"NONE"}""",
            mapsObservedAt.AddMinutes(1));
        _ = await fixture.WarNormalization.NormalizeAsync(
            futureWar.Id,
            TestContext.Current.CancellationToken);

        var replay = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Deferred,
            replay.Status);
        Assert.Equal(
            0L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.RegionNormalizer));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task LateFencedWarFetchDoesNotOverrideAuthoritativeWarContext()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 11, 10, 0, TimeSpan.Zero);

        var authoritativeWar = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "authoritative-war",
            """{"warId":"authoritative-war","warNumber":129,"winner":"WARDENS"}""",
            start);
        var authoritativeCanonical =
            await fixture.WarNormalization.NormalizeAsync(
                authoritativeWar.Id,
                TestContext.Current.CancellationToken);
        Assert.NotNull(authoritativeCanonical.Canonical);

        var lateWar = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "synthetic-late-war",
            """{"warId":"late-war","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(5));
        await fixture.MarkParseAttemptOutcomeAsync(
            lateWar.Id,
            "captured_late");

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-after-late-war",
            """["DeadLandsHex"]""",
            start.AddMinutes(6));

        var membership = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Normalized,
            membership.Status);
        Assert.NotNull(membership.Canonical);

        var region = Assert.Single(membership.Canonical.Memberships);
        Assert.Equal(
            authoritativeCanonical.Canonical.War.Id,
            region.Membership.WarId);
    }

    [Fact]
    public async Task LatestDurableWarParseWinsEvenWhenCanonicalWarNormalizationIsDelayed()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 11, 30, 0, TimeSpan.Zero);

        var olderWar = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "older-war",
            """{"warId":"older-war","warNumber":129,"winner":"WARDENS"}""",
            start);
        var olderCanonical = await fixture.WarNormalization.NormalizeAsync(
            olderWar.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(olderCanonical.Canonical);

        var newerWar = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "newer-war-not-yet-canonical",
            """{"warId":"newer-war","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(10));

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-after-newer-war-evidence",
            """["DeadLandsHex"]""",
            start.AddMinutes(11));

        var membership = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Normalized,
            membership.Status);
        Assert.NotNull(membership.Canonical);

        var newerCanonical = await fixture.WarNormalization.NormalizeAsync(
            newerWar.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(newerCanonical.Canonical);

        var region = Assert.Single(membership.Canonical.Memberships);
        Assert.Equal(
            newerCanonical.Canonical.War.Id,
            region.Membership.WarId);
        Assert.NotEqual(
            olderCanonical.Canonical.War.Id,
            region.Membership.WarId);
    }

    [Fact]
    public async Task SameSourceMapAcrossWarsReusesRegionButKeepsMembershipWarScoped()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        var warOne = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-one",
            """{"warId":"war-one","warNumber":129,"winner":"WARDENS"}""",
            start);
        var warOneResult = await fixture.WarNormalization.NormalizeAsync(
            warOne.Id,
            TestContext.Current.CancellationToken);

        var mapsOne = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-one",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));
        var membershipOne = await fixture.RegionNormalization.NormalizeAsync(
            mapsOne.Id,
            TestContext.Current.CancellationToken);

        var warTwo = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-two",
            """{"warId":"war-two","warNumber":130,"winner":"NONE"}""",
            start.AddHours(1));
        var warTwoResult = await fixture.WarNormalization.NormalizeAsync(
            warTwo.Id,
            TestContext.Current.CancellationToken);

        var mapsTwo = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-two",
            """["DeadLandsHex"]""",
            start.AddHours(1).AddMinutes(1));
        var membershipTwo = await fixture.RegionNormalization.NormalizeAsync(
            mapsTwo.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(warOneResult.Canonical);
        Assert.NotNull(warTwoResult.Canonical);
        Assert.NotNull(membershipOne.Canonical);
        Assert.NotNull(membershipTwo.Canonical);

        var first = Assert.Single(membershipOne.Canonical.Memberships);
        var second = Assert.Single(membershipTwo.Canonical.Memberships);

        Assert.Equal(first.Region.Id, second.Region.Id);
        Assert.NotEqual(first.Membership.Id, second.Membership.Id);
        Assert.Equal(
            warOneResult.Canonical.War.Id,
            first.Membership.WarId);
        Assert.Equal(
            warTwoResult.Canonical.War.Id,
            second.Membership.WarId);
        Assert.Equal(1L, await fixture.CountAsync("runtime.regions"));
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task InvalidMapIdentityRejectsWholeNormalizationWithoutPartialMembership()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 13, 0, 0, TimeSpan.Zero);

        var war = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-invalid-map",
            """{"warId":"war-invalid-map","warNumber":129,"winner":"NONE"}""",
            start);
        _ = await fixture.WarNormalization.NormalizeAsync(
            war.Id,
            TestContext.Current.CancellationToken);

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "maps-invalid",
            """["DeadLandsHex","bad/name"]""",
            start.AddMinutes(1));

        var result = await fixture.RegionNormalization.NormalizeAsync(
            maps.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiRegionNormalizationStatus.Rejected,
            result.Status);
        Assert.Equal(
            "invalid_source_map_name",
            result.NormalizationRun!.ErrorCode);
        Assert.Equal(0L, await fixture.CountAsync("runtime.regions"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_regions"));
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

        var warNormalization = new WarApiWarNormalizationCoordinator(
            canonicalEvidence,
            new WarCanonicalKernel(
                new PostgresWarCanonicalStore(dataSource)),
            normalization,
            options,
            TimeProvider.System);
        var regionNormalization = new WarApiRegionNormalizationCoordinator(
            canonicalEvidence,
            new PostgresWarContextReader(dataSource),
            warNormalization,
            new RegionCanonicalKernel(
                new PostgresRegionCanonicalStore(dataSource)),
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
            warNormalization,
            regionNormalization);
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
            UserAgent: "FoxData-Test/M5-E");

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        FoxData.Core.Sources.SourceId sourceId,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        WarApiWarNormalizationCoordinator warNormalization,
        WarApiRegionNormalizationCoordinator regionNormalization)
        : IAsyncDisposable
    {
        public WarApiWarNormalizationCoordinator WarNormalization { get; } =
            warNormalization;

        public WarApiRegionNormalizationCoordinator RegionNormalization { get; } =
            regionNormalization;

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
                    "\"m5-e\"",
                    "max-age=60",
                    retrievedAt.AddMinutes(1),
                    2),
                body,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);

            var parsed = new WarApiParser().Parse(
                sourceEndpoint.Capability,
                body);
            Assert.True(parsed.Parsed);

            var parseStartedAt = retrievedAt.AddMilliseconds(1);
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

        public async Task<WarRegionDescriptor> ReadWarRegionAsync(
            string sourceMapName)
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT
                    id, war_id, region_id, source_map_name, source_region_id,
                    first_seen_at, last_seen_at, created_at
                FROM runtime.war_regions
                WHERE source_map_name = @source_map_name
                ORDER BY created_at
                LIMIT 1;
                """);
            command.Parameters.AddWithValue(
                "source_map_name",
                sourceMapName);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(
                await reader.ReadAsync(
                    TestContext.Current.CancellationToken));

            return new WarRegionDescriptor(
                new FoxData.Core.Runtime.WarRegionId(reader.GetGuid(0)),
                new FoxData.Core.Runtime.WarId(reader.GetGuid(1)),
                new FoxData.Core.Runtime.RegionId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }

        public async Task<long> CountNormalizationRunsAsync(
            string normalizerVersion)
        {
            await using var command = dataSource.CreateCommand(
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
            await using var command = dataSource.CreateCommand(
                $"SELECT COUNT(*) FROM {tableName};");
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }
}
